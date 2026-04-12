// Terminal frontend: xterm.js + FitAddon + WebView2 messaging bridge.
(function () {
	"use strict";

	var Terminal = window.Terminal;
	var FitAddon = window.FitAddon.FitAddon;

	var terminal = new Terminal({
		fontSize: 14,
		fontFamily: "Cascadia Code, Consolas, Courier New, monospace",
		cursorBlink: true,
		theme: {
			background: "#1e1e1e",
			foreground: "#cccccc",
			cursor: "#aeafad",
			selectionBackground: "#264f78"
		}
	});

	var fitAddon = new FitAddon();
	terminal.loadAddon(fitAddon);
	terminal.open(document.getElementById("terminal"));
	fitAddon.fit();

	var lastCols = terminal.cols;
	var lastRows = terminal.rows;

	// Notify C# of terminal dimensions after fit
	function sendResize() {
		var cols = terminal.cols;
		var rows = terminal.rows;
		if (cols !== lastCols || rows !== lastRows) {
			lastCols = cols;
			lastRows = rows;
			window.chrome.webview.postMessage(
				JSON.stringify({ type: "resize", cols: cols, rows: rows })
			);
		}
	}

	// Send initial size
	window.chrome.webview.postMessage(
		JSON.stringify({ type: "resize", cols: terminal.cols, rows: terminal.rows })
	);

	// Handle container resize
	var resizeTimer = null;
	window.addEventListener("resize", function () {
		clearTimeout(resizeTimer);
		resizeTimer = setTimeout(function () {
			fitAddon.fit();
			sendResize();
		}, 50);
	});

	// Forward user input to C#
	terminal.onData(function (data) {
		window.chrome.webview.postMessage(
			JSON.stringify({ type: "input", data: data })
		);
	});

	// Receive messages from C# (terminal output, clear, etc.)
	window.chrome.webview.addEventListener("message", function (event) {
		var msg;
		try {
			msg = typeof event.data === "string" ? JSON.parse(event.data) : event.data;
		} catch (e) {
			return;
		}

		if (msg.type === "output") {
			terminal.write(msg.data);
		} else if (msg.type === "clear") {
			terminal.clear();
		}
	});

	// Expose terminal instance for C# focus recovery (ExecuteScriptAsync)
	window.term = terminal;

	// Focus terminal on click
	document.addEventListener("click", function () {
		terminal.focus();
	});

	// Auto-focus on load
	terminal.focus();
})();
