// Browser view (a session view): the chat's browser tab live, with the user's mouse and keys going to the page.
import { createTab } from '@netpi/kit';
import BrowserView from './BrowserView.svelte';

export const mount = createTab(BrowserView);
