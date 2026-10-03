// Mcp tab: the configured MCP servers, their tools and what is exposed to a chat.
import { createTab } from '@netpi/kit';
import McpTab from './McpTab.svelte';

export const mount = createTab(McpTab);