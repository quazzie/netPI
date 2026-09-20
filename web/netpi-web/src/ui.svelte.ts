export type FontChoice = "system" | "inter" | "mono" | "serif";

const KEY = "netpi.ui.v2";

interface PersistedUi {
  leftOpen?: boolean;
  rightOpen?: boolean;
  leftWidth?: number;
  rightWidth?: number;
  rightTab?: string;
  keepThinkingOpen?: boolean;
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
  leftOpen = $state(initial.leftOpen ?? true);
  rightOpen = $state(initial.rightOpen ?? true);
  leftWidth = $state(initial.leftWidth ?? 260);
  rightWidth = $state(initial.rightWidth ?? 330);
  rightTab = $state(initial.rightTab ?? "plugins");
  leftPage = $state<"sessions" | "settings">("sessions");
  keepThinkingOpen = $state(initial.keepThinkingOpen ?? false);
  font = $state<FontChoice>(initial.font ?? "system");

  constructor() {
    if (typeof document !== "undefined")
      document.documentElement.dataset.font = this.font;
  }

  private persist() {
    if (typeof localStorage === "undefined") return;
    const snapshot: PersistedUi = {
      leftOpen: this.leftOpen,
      rightOpen: this.rightOpen,
      leftWidth: this.leftWidth,
      rightWidth: this.rightWidth,
      rightTab: this.rightTab,
      keepThinkingOpen: this.keepThinkingOpen,
      font: this.font,
    };
    try { localStorage.setItem(KEY, JSON.stringify(snapshot)); } catch {}
  }

  toggleLeft() {
    this.leftOpen = !this.leftOpen;
    this.persist();
  }

  toggleRight() {
    this.rightOpen = !this.rightOpen;
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
}

export const ui = new UiSettings();
