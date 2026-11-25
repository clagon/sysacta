// Interop check with an independent MCP client implementation (official TypeScript SDK).
// Usage: node mcp-client.mjs <url> <token>. The token must be granted only audio_get_state.
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";

const [url, token] = process.argv.slice(2);
const fail = (message) => { console.error(message); process.exit(1); };

const transport = new StreamableHTTPClientTransport(new URL(url), {
  requestInit: { headers: { Authorization: `Bearer ${token}` } },
});
const client = new Client({ name: "sysacta-ci", version: "1.0.0" });
await client.connect(transport);

const tools = (await client.listTools()).tools.map((t) => t.name).sort();
if (JSON.stringify(tools) !== JSON.stringify(["audio_get_state"])) fail(`unexpected tools/list: ${tools}`);
console.log(`tools/list: ${tools.join(", ")}`);

const state = await client.callTool({ name: "audio_get_state", arguments: {} });
const s = state.structuredContent;
if (!s || !s.request_id) fail(`missing structured result: ${JSON.stringify(state)}`);
// CI runners usually have no audio device, so a structured TARGET_NOT_FOUND is as valid as success.
if (!(s.status === "success" || (s.status === "error" && ["TARGET_NOT_FOUND", "DEVICE_DISCONNECTED", "OS_API_ERROR"].includes(s.error?.code))))
  fail(`unexpected audio_get_state result: ${JSON.stringify(s)}`);
console.log(`audio_get_state: ${s.status}${s.error ? " " + s.error.code : ""}`);

const denied = await client.callTool({ name: "audio_set_volume", arguments: { level: 10 } });
if (!denied.isError || denied.structuredContent?.error?.code !== "PERMISSION_DENIED") fail(`direct call was not denied: ${JSON.stringify(denied)}`);
console.log("audio_set_volume (not granted): PERMISSION_DENIED");

await client.close();
