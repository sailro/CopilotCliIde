#!/usr/bin/env node
// tools/mcp-call.mjs — Call an MCP tool on a VS Code named pipe
//
// Usage:
//   node tools/mcp-call.mjs <tool_name> [arguments_json]
//   node tools/mcp-call.mjs get_selection
//   node tools/mcp-call.mjs get_diagnostics '{"uri":""}'
//   node tools/mcp-call.mjs open_diff '{"original_file_path":"c:\\file.cs","new_file_contents":"...","tab_name":"test"}'
//
// Discovers the pipe from ~/.copilot/ide/*.lock automatically.
// Performs the full MCP handshake (initialize → notifications/initialized → tools/call)
// and prints the tool response JSON to stdout.

import http from 'http';
import { readFileSync, readdirSync } from 'fs';
import { join } from 'path';

const toolName = process.argv[2];
if (!toolName) {
  console.error('Usage: node tools/mcp-call.mjs <tool_name> [arguments_json]');
  process.exit(1);
}

const toolArgs = process.argv[3] ? JSON.parse(process.argv[3]) : {};
const timeout = toolName === 'open_diff' ? 3600000 : 30000;

// --- Discover pipe from lock file ---
const ideDir = join(process.env.USERPROFILE || process.env.HOME, '.copilot', 'ide');
const lockFiles = readdirSync(ideDir).filter(f => f.endsWith('.lock'));
if (lockFiles.length === 0) {
  console.error('No lock files found in', ideDir);
  process.exit(1);
}

const lock = JSON.parse(readFileSync(join(ideDir, lockFiles[0]), 'utf8'));
const pipePath = lock.socketPath.replace(/^\\\\\.\\pipe\\/, '//./pipe/');
const auth = lock.headers.Authorization;
let sessionId = null;
console.error(`Pipe: ${pipePath}`);
console.error(`Tool: ${toolName}`);

function extractRpcMessages(payload) {
  const messages = [];
  for (const line of payload.split(/\r?\n/)) {
    if (!line.startsWith('data:')) {
      continue;
    }

    const json = line.slice(5).trim();
    if (!json) {
      continue;
    }

    try {
      messages.push(JSON.parse(json));
    } catch {
      // Ignore partial or non-JSON event payloads.
    }
  }
  return messages;
}

// --- HTTP request helper ---
function mcpPost(requestBody) {
  return new Promise((resolve, reject) => {
    const data = JSON.stringify(requestBody);
    const headers = {
      'Host': 'localhost',
      'Authorization': auth,
      'Content-Type': 'application/json',
      'Accept': 'text/event-stream, application/json',
      'Content-Length': Buffer.byteLength(data),
      'Connection': 'keep-alive'
    };

    if (sessionId) {
      headers['Mcp-Session-Id'] = sessionId;
    }

    const req = http.request({
      socketPath: pipePath,
      path: '/mcp',
      method: 'POST',
      headers,
      timeout
    }, (res) => {
      let responseBody = '';
      res.on('data', c => {
        responseBody += c;

        // For SSE streams, resolve as soon as we see our matching response.
        if (requestBody.id !== undefined) {
          const messages = extractRpcMessages(responseBody);
          if (messages.some(message => message.id === requestBody.id)) {
            resolve({ status: res.statusCode, headers: res.headers, body: responseBody });
            res.destroy(); // stop reading
          }
        }
      });
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body: responseBody }));
    });
    req.on('error', reject);
    req.on('timeout', () => { req.destroy(); reject(new Error('timeout')); });
    req.write(data);
    req.end();
  });
}

// --- Main ---
try {
  // Step 1: Initialize
  const initRes = await mcpPost({
    jsonrpc: '2.0', id: 0, method: 'initialize',
    params: { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'mcp-call', version: '1.0' } }
  });
  if (initRes.status !== 200) {
    console.error('Initialize failed:', initRes.status, initRes.body);
    process.exit(1);
  }

  const initSessionHeader = initRes.headers['mcp-session-id'];
  sessionId = Array.isArray(initSessionHeader) ? initSessionHeader[0] : initSessionHeader;
  if (!sessionId) {
    console.error('Initialize response missing Mcp-Session-Id header.');
    process.exit(1);
  }
  console.error(`Session: ${sessionId}`);

  // Step 2: notifications/initialized
  await mcpPost({ jsonrpc: '2.0', method: 'notifications/initialized' });

  // Step 3: tools/call
  console.error(`Calling ${toolName}...`);
  const toolRes = await mcpPost({
    jsonrpc: '2.0', id: 1, method: 'tools/call',
    params: { name: toolName, arguments: toolArgs }
  });

  const messages = extractRpcMessages(toolRes.body);
  const rpc = messages.find(message => message.id === 1) ?? messages.find(message => message.result || message.error);
  if (!rpc) {
    console.error('No data in response. Status:', toolRes.status);
    console.error('Body:', toolRes.body);
    process.exit(1);
  }

  const content = rpc.result?.content;
  if (content?.[0]?.text) {
    try {
      console.log(JSON.stringify(JSON.parse(content[0].text), null, 2));
    } catch {
      console.log(content[0].text);
    }
  } else {
    console.log(JSON.stringify(rpc.result || rpc.error, null, 2));
  }
} catch (e) {
  console.error('Error:', e.message);
  process.exit(1);
}
