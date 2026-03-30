using CopilotCliIde.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Collections.Generic;
using System.Collections.Concurrent;

namespace CopilotCliIde.Server;

/// <summary>
/// ASP.NET Core MCP host over a named pipe transport.
/// </summary>
public sealed class AspNetMcpPipeServer : IAsyncDisposable
{
	private const int PipeStartupDelayMs = 200;
	private const string AuthHeader = "authorization";
	private const string SessionIdHeader = "mcp-session-id";
	private const string GetStreamId = "__get__";

	private WebApplication? _app;
	private CancellationTokenSource? _cts;
	private RpcClient? _rpcClient;
	private string? _nonce;
	private TrackingSseEventStreamStore? _eventStreamStore;
	private readonly ConcurrentDictionary<string, McpServer> _activeSessions = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, byte> _initializedSessions = new(StringComparer.Ordinal);

	public async Task StartAsync(RpcClient rpcClient, string pipeName, string nonce, CancellationToken ct)
	{
		_rpcClient = rpcClient;
		_nonce = nonce;
		_cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		_eventStreamStore = new TrackingSseEventStreamStore(PushInitialStateAsync);

		var builder = WebApplication.CreateSlimBuilder();
		builder.WebHost.ConfigureKestrel(options =>
		{
			options.ListenNamedPipe(pipeName, listenOptions =>
			{
				listenOptions.Protocols = HttpProtocols.Http1;
			});
		});

		builder.Services.AddSingleton(rpcClient);
		builder.Services
			.AddMcpServer(options =>
			{
				options.ServerInfo = new Implementation { Name = "vscode-copilot-cli", Version = "0.0.1", Title = "VS Code Copilot CLI" };
				options.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } };
			})
			.WithHttpTransport(options =>
			{
				options.Stateless = false;
				options.EventStreamStore = _eventStreamStore;
			})
			.WithToolsFromAssembly(typeof(AspNetMcpPipeServer).Assembly);

		_app = builder.Build();

		_app.Use(async (ctx, next) =>
		{
			if (!ctx.Request.Headers.TryGetValue(AuthHeader, out var auth) || auth != $"Nonce {_nonce}")
			{
				ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
				await ctx.Response.WriteAsync("Unauthorized", cancellationToken: ct);
				return;
			}

			await next();
		});

		_app.Use(async (ctx, next) =>
		{
			await next();
			if (ctx.Request.Method == HttpMethods.Delete
				&& (ctx.Request.Path == "/" || ctx.Request.Path == "/mcp"))
			{
				var deleteSessionId = ctx.Request.Headers[SessionIdHeader].ToString();
				_eventStreamStore?.RemoveSession(deleteSessionId);
				if (!string.IsNullOrWhiteSpace(deleteSessionId))
				{
					_initializedSessions.TryRemove(deleteSessionId, out _);
					_activeSessions.TryRemove(deleteSessionId, out _);
				}
			}

			if (ctx.Request.Path == "/" || ctx.Request.Path == "/mcp")
			{
				var sessionId = ctx.Response.Headers[SessionIdHeader].ToString();
				if (string.IsNullOrWhiteSpace(sessionId))
				{
					sessionId = ctx.Request.Headers[SessionIdHeader].ToString();
				}

				if (!string.IsNullOrWhiteSpace(sessionId))
				{
					ctx.Response.Headers[SessionIdHeader] = sessionId;

					if (ctx.Request.Method != HttpMethods.Delete
						&& ctx.Features.Get<McpServer>() is { } sessionServer)
					{
						_activeSessions[sessionId] = sessionServer;
					}
				}
			}
		});

		_app.MapMcp("/");
		_app.MapMcp("/mcp");

		await _app.StartAsync(_cts.Token);

		await Task.Delay(PipeStartupDelayMs, ct);
	}

	public ValueTask DisposeAsync()
	{
		if (_cts == null)
		{
			return ValueTask.CompletedTask;
		}

		_cts.Cancel();
		if (_app != null)
		{
			try
			{
				_app.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
			}
			catch { /* Ignore */ }
		}
		_cts.Dispose();
		_cts = null;
		return ValueTask.CompletedTask;
	}

	public Task PushSelectionChangedAsync(SelectionNotification notification)
	{
		return PushNotificationAsync(Notification.SelectionChanged, new
		{
			text = notification.Text ?? "",
			filePath = notification.FilePath,
			fileUrl = notification.FileUrl,
			selection = notification.Selection == null ? null : new
			{
				start = new { line = notification.Selection.Start?.Line ?? 0, character = notification.Selection.Start?.Character ?? 0 },
				end = new { line = notification.Selection.End?.Line ?? 0, character = notification.Selection.End?.Character ?? 0 },
				isEmpty = notification.Selection.IsEmpty
			}
		});
	}

	public Task PushDiagnosticsChangedAsync(DiagnosticsChangedNotification notification)
	{
		return PushNotificationAsync(Notification.DiagnosticsChanged, new
		{
			uris = notification.Uris?.Select(u => new
			{
				uri = u.Uri,
				diagnostics = u.Diagnostics?.Select(d => new
				{
					range = d.Range == null ? null : new
					{
						start = new { line = d.Range.Start?.Line ?? 0, character = d.Range.Start?.Character ?? 0 },
						end = new { line = d.Range.End?.Line ?? 0, character = d.Range.End?.Character ?? 0 }
					},
					message = d.Message,
					severity = d.Severity,
					code = d.Code
				})
			})
		});
	}

	public async Task PushNotificationAsync(string method, object? @params)
	{
		var sessions = _activeSessions.ToArray();
		if (sessions.Length == 0)
		{
			return;
		}

		foreach (var (sessionId, session) in sessions)
		{
			try
			{
				if (@params == null)
				{
					await session.SendNotificationAsync(method, CancellationToken.None);
				}
				else
				{
					await session.SendNotificationAsync(method, @params, cancellationToken: CancellationToken.None);
				}
			}
			catch (ObjectDisposedException)
			{
				_activeSessions.TryRemove(sessionId, out _);
			}
			catch (InvalidOperationException)
			{
				_activeSessions.TryRemove(sessionId, out _);
			}
		}
	}

	private async Task PushInitialStateAsync(string sessionId, string streamId)
	{
		// Only treat the long-lived GET SSE listener as a "client connect" for initial state.
		// POST response streams are ephemeral and should not trigger initial-state fetches.
		if (!string.Equals(streamId, GetStreamId, StringComparison.Ordinal)
			|| string.IsNullOrWhiteSpace(sessionId))
		{
			return;
		}

		if (!_initializedSessions.TryAdd(sessionId, 0))
		{
			return;
		}

		try { await _rpcClient!.VsServices!.ResetNotificationStateAsync(); }
		catch { /* VS not ready */ }

		await PushCurrentSelectionAsync();
		await PushCurrentDiagnosticsAsync();
	}

	private async Task PushCurrentSelectionAsync()
	{
		try
		{
			var sel = await _rpcClient!.VsServices!.GetSelectionAsync();
			if (string.IsNullOrEmpty(sel.FilePath))
			{
				return;
			}

			await PushSelectionChangedAsync(new SelectionNotification
			{
				Text = sel.Text,
				FilePath = sel.FilePath,
				FileUrl = sel.FileUrl,
				Selection = sel.Selection
			});
		}
		catch { /* VS not ready or no active editor — nothing to push */ }
	}

	private async Task PushCurrentDiagnosticsAsync()
	{
		try
		{
			var diag = await _rpcClient!.VsServices!.GetDiagnosticsAsync(null);
			if (diag.Files == null || diag.Files.Count == 0)
			{
				return;
			}

			await PushDiagnosticsChangedAsync(new DiagnosticsChangedNotification
			{
				Uris = [.. diag.Files.Select(f => new DiagnosticsChangedUri
				{
					Uri = f.Uri,
					Diagnostics = f.Diagnostics
				})]
			});
		}
		catch { /* VS not ready or no diagnostics */ }
	}
}
