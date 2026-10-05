/**
 * The ideas the welcome screen offers (idea-ky14bu): the plugin's own `ideas.picks`, read once per window and again
 * whenever the backlog changes somewhere, so an idea finished in another window disappears here.
 *
 * The welcome screen is host UI and the backlog is a plugin's, so nothing here knows what makes an idea worth showing:
 * `ideas.picks` returns a small ranked list (this project's open ideas first, then the global ones) and a window that
 * has no Ideas plugin (or an older one, without the method) simply offers nothing.
 */
import { rpc } from '../rpc.svelte.js';

const LIMIT = 3; // a screen is not a list

class WelcomeIdeas {
  /** [{ id, title, summary, status, priority, projectId, projectName, updatedAt }], in the plugin's order */
  picks = $state([]);
  enabled = $state(true);
  loaded = $state(false);
  projectId = $state(null); // the project the welcome screen starts sessions in (null: none)
  shown = false; // the start screen is on (Welcome.svelte sets it): the only time a changed backlog is worth reading now

  /** Read once per window, and again for a different target project (or a stale one, when forced). */
  async load(projectId, force = false) {
    if (!this.enabled) return;
    const target = projectId ?? null;
    if (this.loaded && this.projectId === target && !force) return;
    this.projectId = target;
    try {
      const res = await rpc('ideas.picks', { projectId: target, limit: LIMIT });
      this.picks = res?.picks ?? [];
      this.loaded = true;
    } catch (e) {
      // No Ideas plugin, or an older one: stop asking in this window. Anything else is a transport hiccup, and the
      // next ideas.changed or the next time the screen is shown tries again.
      if (/unknown method/i.test(e.message ?? '')) {
        this.enabled = false;
        this.picks = [];
      } else this.loaded = false;
    }
  }

  /**
   * ideas.changed: the backlog moved (in this window or another), so the offer moves with it — now while the start
   * screen is showing; while a chat is in front it only forgets what it has, and the screen reads again when it is
   * shown (ideas.changed arrives after every write, an agent's too: a screen nobody sees must not cost an ideas.picks each).
   */
  changed() {
    if (!this.enabled) return;
    if (this.shown) this.load(this.projectId, true);
    else this.loaded = false;
  }
}

export const welcomeIdeas = new WelcomeIdeas();
