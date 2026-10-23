// Autonomy tab (netpi.autonomy): what chats nobody watches were refused, what they changed (with undo), and the grants.
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { createTab } from '@netpi/kit';
import AutonomyTab from './AutonomyTab.svelte';

export const mount = createTab(AutonomyTab);
