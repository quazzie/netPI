// Phone tab (netpi.phone): state of the phone listener, pairing (code + QR) and the paired devices.
// Built by `npm run build:plugins` into ../wwwroot/ui.js.
import { createTab } from '@netpi/kit';
import PhoneTab from './PhoneTab.svelte';

export const mount = createTab(PhoneTab);
