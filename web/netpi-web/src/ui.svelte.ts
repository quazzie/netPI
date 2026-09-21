export type FontChoice = "system" | "inter" | "mono" | "serif";

const KEY = "netpi.ui.v2";

interface PersistedUi {
  rightOpen?: boolean;
  rightWidth?: number;
  rightTab?: string;
  keepThinkingOpen?: boolean;
  keepToolsOpen?: boolean;
  font?: FontChoice;
}

function load(): PersistedUi {
  try {
    return JSON.parse(localStorage.getItem(KEY) || "{}") as PersistedUi;
  } catch {
    return {};
  }
}

const initial: PersistedUi =
  typeof localStorage === "undefined" ? {} : load();

class UiSettings {
  rightOpen = $state(initial.rightOpen ?? true);
  rightWidth = $state(initial.rightWidth ?? 330);
  rightTab = $state(initial.rightTab ?? "diagnostics");

  /** astra-1 G1: monotonically increasing request counter — set by commands
   *  (Composer) to ask the shell to open the settings dialog. A rune so the
   *  shell's $effect re-runs (reactively) on each increment. */
  settingsRequest = $state(0);
  keepThinkingOpen = $state(initial.keepThinkingOpen ?? false);
  keepToolsOpen = $state(initial.keepToolsOpen ?? false);
  font = $state<FontChoice>(initial.font ?? "system");

  requestSettings(): void {
    this.settingsRequest += 1;
  }

  constructor() {
    if (typeof document !== "undefined")
      document.documentElement.dataset.font = this.font;
  }

  private persist() {
    if (typeof localStorage === "undefined") return;
    const snapshot: PersistedUi = {
      rightOpen: this.rightOpen,
      rightWidth: this.rightWidth,
      rightTab: this.rightTab,
      keepThinkingOpen: this.keepThinkingOpen,
      keepToolsOpen: this.keepToolsOpen,
      font: this.font,
    };
    try { localStorage.setItem(KEY, JSON.stringify(snapshot)); } catch {}
  }

  toggleRight() {
    this.rightOpen = !this.rightOpen;
    this.persist();
  }

  setRightWidth(value: number, persist = true) {
    this.rightWidth = Math.round(Math.max(260, Math.min(760, value)));
    if (persist) this.persist();
  }

  persistLayout() {
    this.persist();
  }

  setRightTab(id: string, open = true) {
    this.rightTab = id;
    if (open) this.rightOpen = true;
    this.persist();
  }

  setFont(value: FontChoice) {
    this.font = value;
    if (typeof document !== "undefined")
      document.documentElement.dataset.font = value;
    this.persist();
  }

  setKeepThinkingOpen(value: boolean) {
    this.keepThinkingOpen = value;
    this.persist();
  }

  setKeepToolsOpen(value: boolean) {
    this.keepToolsOpen = value;
    this.persist();
  }
}

export const ui = new UiSettings();
