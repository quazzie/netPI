#!/usr/bin/env node
// The Ideas tab, the idea dialog and the start screen's ideas against a REAL netpi-server (test "ui.ideas-tab", moved from
// the mock suite's sections "plugin tab: Ideas" and "welcome screen: ideas to work on"). Meaning search is ui.idea-meaning
// (it needs an embeddings server, which the E2E server has not).
//
//   node tests/NetPI.E2E/ui/ideas-tab.mjs --url http://127.0.0.1:7470 --token e2e-token --work <dir> [--out dir]
import { run, check, shot, openApp, openSession, openStripTab, rpc, send, newDir, STAMP } from './lib.mjs';

const project = await rpc('projects.create', { name: `ideas-${STAMP}`, path: newDir(`ideas-${STAMP}`) });
const chat = await rpc('sessions.create', { title: `Ideas chat ${STAMP}`, projectId: project.id });
await send(chat.id, 'hello [s:echo]');
const seeded = [];
const seed = async (title, status, tags, summary = `About ${title.toLowerCase()}.`) =>
  seeded.push((await rpc('ideas.add', { projectId: project.id, sessionId: chat.id, idea: { title, summary, status, tags } })).id);
// newest first in a group: the open ones are added in reverse of the order the list shows them
await seed('Semantic search', 'parked', ['search']);
await seed('Stream compaction summaries', 'in-progress', ['context']);
await seed('Cache the model list', 'in-progress', ['aiproxy']);
await seed('Export chats as Markdown', 'planned', ['docs']);
await seed('Retry budget per provider', 'open', ['aiproxy']);
await seed('Dark mode toggle', 'open', ['ui']);

await run('ideas-tab', async ({ page, socket }) => {
  try {
    await openApp(page);
    await openSession(page, `Ideas chat ${STAMP}`);
    const ta = page.locator('.composer textarea');

    // ---- plugin tab: Ideas
    await openStripTab(page, 'right', 'Ideas');
    await page.locator('.plugin-root .ideas .card').first().waitFor({ timeout: 10_000 });
    const cards = () => page.locator('.ideas .card');
    const n0 = await cards().count();
    check('ideas: project backlog listed', n0 >= 4, `${n0} active ideas`);
    // a short list still fills the panel: the footer sits at its bottom, not under the last card
    const footGap = await page.evaluate(() => {
      const foot = document.querySelector('.plugin-root .ideas .foot');
      const mount = foot?.closest('.mount');
      return foot && mount ? Math.round(mount.getBoundingClientRect().bottom - foot.getBoundingClientRect().bottom) : null;
    });
    check('ideas: the tab fills the panel (footer at the bottom)', footGap !== null && Math.abs(footGap) <= 4, `gap ${footGap}px`);
    // A card is the idea's title and a short summary; a click opens the whole idea in the host's idea dialog. The card's ⋯
    // menu ("Status and actions") holds the status and every action. The list is grouped by status, so every step names
    // its card: a status change moves a card into another group.
    const dlg = page.locator('.idea-dialog');
    const openCard = async (card) => {
      await card.locator('.main').click();
      await dlg.waitFor({ timeout: 5_000 }).catch(() => {});
      await page.waitForTimeout(300); // past the dialog's fade-in
    };
    const closeDialog = async () => {
      await page.keyboard.press('Escape');
      await dlg.waitFor({ state: 'detached', timeout: 5_000 }).catch(() => {});
    };
    const moreMenu = async (card, item) => {
      await card.locator('button[title="Status and actions"]').click();
      await page.locator('.np-menu .np-menu-item', { hasText: item }).first().click();
    };
    const cardTitled = (t) => page.locator('.ideas .card', { hasText: t }).first();
    const openTitles = () => cards().evaluateAll((els) => els.filter((e) => e.dataset.status === 'open').map((e) => e.querySelector('.title')?.textContent?.trim()));
    const firstTitle = (await cards().first().locator('.title').innerText()).trim();
    check('ideas: a card is its title and a short summary, nothing to expand', (await cards().first().locator('.blurb').count()) === 1 && (await cards().first().locator('.sec').count()) === 0);
    await openCard(cards().first());
    // an idea added over the RPC has its summary as a section
    check('ideas: a click opens the idea dialog with its sections', (await dlg.count()) === 1 && (await dlg.locator('.i-title').inputValue()) === firstTitle, firstTitle);
    await shot(page, 'ui-ideas-01-tab');
    await closeDialog();
    check('ideas: Escape closes it again', (await dlg.count()) === 0);
    await moreMenu(cardTitled('Dark mode toggle'), 'Status: in-progress');
    await page.locator('.ideas .card[data-status="in-progress"]', { hasText: 'Dark mode toggle' }).waitFor({ timeout: 5_000 }).catch(() => {});
    const darkId = seeded[5];
    check('ideas: status change (the card menu)', (await cardTitled('Dark mode toggle').getAttribute('data-status')) === 'in-progress',
      `card ${await cardTitled('Dark mode toggle').getAttribute('data-status')}, server ${(await rpc('ideas.get', { id: darkId })).status}`);
    await moreMenu(cardTitled('Dark mode toggle'), 'Status: open');
    // filing is the host's idea dialog (the tab's "+", Ctrl+I and /idea all open it), on the project the tab shows
    await page.locator('.ideas .scope button[title^="New idea"]').click();
    await dlg.waitFor({ timeout: 5_000 }).catch(() => {});
    check('ideas: the New idea button opens the idea dialog', (await dlg.count()) === 1);
    check('ideas: a new idea goes to the project the tab shows', (await dlg.locator('.i-project').inputValue()) === project.id, await dlg.locator('.i-project').inputValue());
    await dlg.locator('.i-title').fill('Keyboard shortcuts cheat sheet');
    await dlg.locator('.i-tags').fill('ui, docs');
    // an image attached while filing: pasted, shrunk to fit, stored by the host, and kept on the idea
    const ref = await rpc('ideas.addImage', { data: 'aGVsbG8=', mediaType: 'image/png', name: 'tiny.png' });
    check('ideas: the host stores an image and returns a reference', /idea-images[\\/]img-/.test(ref?.path ?? ''), JSON.stringify(ref));
    await page.evaluate(() => {
      const canvas = document.createElement('canvas');
      canvas.width = 1200;
      canvas.height = 900;
      const ctx = canvas.getContext('2d');
      const img = ctx.createImageData(canvas.width, canvas.height);
      for (let i = 0; i < img.data.length; i += 4) {
        img.data[i] = Math.random() * 256;
        img.data[i + 1] = Math.random() * 256;
        img.data[i + 2] = Math.random() * 256;
        img.data[i + 3] = 255;
      }
      ctx.putImageData(img, 0, 0);
      return canvas.toBlob((blob) => {
        const dt = new DataTransfer();
        dt.items.add(new File([blob], 'shot.png', { type: 'image/png' }));
        document.querySelector('.idea-dialog .i-summary').dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true }));
      }, 'image/png');
    });
    const thumb = await page.waitForSelector('.idea-dialog .thumbs img', { timeout: 15_000 }).catch(() => null);
    const note = await page.locator('.idea-dialog .note').allInnerTexts().catch(() => []);
    check('ideas: a pasted image is attached while filing', !!thumb, note.join('; '));
    await dlg.locator('.i-submit').click();
    await dlg.waitFor({ state: 'detached', timeout: 10_000 }).catch(() => {});
    check('ideas: the dialog closes once the idea is added', (await dlg.count()) === 0);
    const newCard = cardTitled('Keyboard shortcuts cheat sheet');
    await newCard.waitFor({ timeout: 5_000 }).catch(() => {});
    check('ideas: new idea added on top of the open ones', (await newCard.count()) === 1 && (await openTitles())[0] === 'Keyboard shortcuts cheat sheet', (await openTitles()).join(' / '));
    // the image is the idea's: the dialog fetches it back as it opens, and removing it there (and saving) takes it off
    await openCard(newCard);
    await dlg.locator('.thumbs img').first().waitFor({ timeout: 5_000 }).catch(() => {});
    check('ideas: the idea dialog shows the attached image', (await dlg.locator('.thumbs img').count()) === 1);
    const shotSrc = (await dlg.locator('.thumbs img').first().getAttribute('src').catch(() => '')) ?? '';
    check('ideas: the image is fetched back from the host, not kept in the list', shotSrc.startsWith('data:image/'), shotSrc.slice(0, 24));
    const ideaId = (await dlg.locator('.idnote').innerText()).trim();
    const prompt = await rpc('ideas.toPrompt', { id: ideaId });
    check('ideas: the full text hands the image to whoever works on it', /idea-images[\\/]img-/.test(String(prompt)), String(prompt).split('\n').find((l) => l.includes('idea-images')) ?? 'no image line');
    await dlg.locator('.thumbs button[aria-label="Remove image"]').click();
    await dlg.locator('.i-submit').click();
    await dlg.waitFor({ state: 'detached', timeout: 5_000 }).catch(() => {});
    await page.waitForTimeout(400);
    check('ideas: removing the image in the dialog takes it off the idea', ((await rpc('ideas.get', { id: ideaId })).images ?? []).length === 0);
    // sections are what agents write (the ideas tool, ideas.update): the dialog shows them; the tab refetches on ideas.changed
    const mark = socket.mark();
    await rpc('ideas.update', { id: ideaId, patch: { addSections: [{ kind: 'todo', content: '- [ ] list shortcuts\n- [ ] render a table' }] } });
    const reloaded = await socket.waitSent('ideas.list', mark).then(() => true).catch(() => false);
    await page.waitForTimeout(300);
    await openCard(cardTitled('Keyboard shortcuts cheat sheet'));
    const secs = await dlg.locator('.sec').allInnerTexts();
    check('ideas: a section an agent added shows in the dialog', secs.some((t) => /list shortcuts/.test(t)), reloaded ? `${secs.length} sections` : 'the tab never reloaded the list after ideas.changed');
    await closeDialog();
    // send to chat → a pointer in the composer, not the idea's text (the agent reads the idea itself)
    await moreMenu(cardTitled('Keyboard shortcuts cheat sheet'), 'Send to chat');
    await page.waitForTimeout(300);
    const staged = await ta.inputValue();
    check('ideas: send to chat stages a pointer, not the idea', staged.includes(ideaId) && !staged.includes('# Keyboard shortcuts cheat sheet'), staged);
    await moreMenu(newCard, 'Insert the full text');
    await page.waitForTimeout(300);
    check('ideas: insert the full text (ideas.toPrompt)', (await ta.inputValue()).includes('# Keyboard shortcuts cheat sheet'));
    await ta.fill('');
    // Move down moves within the group: the card that was below it is now above it
    const openBefore = await openTitles();
    await moreMenu(newCard, 'Move down');
    await page.waitForFunction((t) => {
      const titles = [...document.querySelectorAll('.ideas .card[data-status="open"] .title')].map((e) => e.textContent.trim());
      return titles.indexOf(t) > 0;
    }, 'Keyboard shortcuts cheat sheet', { timeout: 5_000 }).catch(() => {});
    const openAfter = await openTitles();
    check('ideas: move down (ideas.reorder)', openAfter.indexOf('Keyboard shortcuts cheat sheet') === openBefore.indexOf('Keyboard shortcuts cheat sheet') + 1, `${openBefore.join(' / ')} → ${openAfter.join(' / ')}`);
    await moreMenu(cardTitled('Keyboard shortcuts cheat sheet'), 'Delete idea');
    await page.locator('.dialog button', { hasText: /^Delete$/ }).click();
    await page.locator('.ideas .card', { hasText: 'Keyboard shortcuts cheat sheet' }).waitFor({ state: 'detached', timeout: 5_000 }).catch(() => {});
    check('ideas: delete with confirm', (await page.locator('.ideas .card', { hasText: 'Keyboard shortcuts cheat sheet' }).count()) === 0);

    // ---- the idea dialog: Ctrl+I from anywhere, /idea, Escape, editing (with its conflict) and the agent task
    await ta.click();
    await page.keyboard.press('Control+i');
    await dlg.waitFor({ timeout: 5_000 }).catch(() => {});
    check('ideas: Ctrl+I opens the idea dialog from the composer', (await dlg.count()) === 1);
    check('ideas: the title has the focus', await page.evaluate(() => document.activeElement?.classList.contains('i-title')));
    await closeDialog();
    check('ideas: Escape closes the dialog', (await dlg.count()) === 0);
    await ta.fill('/idea');
    await ta.press('Enter'); // accepts the popup entry
    await page.waitForTimeout(100);
    if ((await ta.inputValue()).startsWith('/idea')) await ta.press('Enter');
    await dlg.waitFor({ timeout: 5_000 }).catch(() => {});
    check('ideas: /idea with no text opens the dialog', (await dlg.count()) === 1);
    await closeDialog();
    await ta.fill('');
    // what the tab shows is where a new idea goes: on "Global" the dialog files under no project, on the project it is the project's
    const pickScope = async (label) => {
      await page.locator('.ideas .scope button[title="Project filter"]').click();
      await page.locator('.np-menu .np-menu-item', { hasText: label }).first().click();
      await page.waitForTimeout(200);
    };
    await pickScope('Global (unbound)');
    await page.locator('.ideas .scope button[title^="New idea"]').click();
    await dlg.waitFor({ timeout: 5_000 }).catch(() => {});
    check('ideas: with the tab on Global a new idea is filed under no project', (await dlg.locator('.i-project').inputValue()) === 'global', await dlg.locator('.i-project').inputValue());
    await closeDialog();
    await pickScope('This project');
    await page.locator('.ideas .scope button[title^="New idea"]').click();
    await dlg.waitFor({ timeout: 5_000 }).catch(() => {});
    check('ideas: with the tab on the active project a new idea goes to it', (await dlg.locator('.i-project').inputValue()) === project.id, await dlg.locator('.i-project').inputValue());
    await closeDialog();

    const edited = await rpc('ideas.add', { projectId: project.id, idea: { title: 'Dialog edit target', summary: 'before', tags: ['t1'] } });
    seeded.push(edited.id);
    const edCard = cardTitled('Dialog edit target');
    await edCard.waitFor({ timeout: 5_000 }).catch(() => {});
    await openCard(edCard);
    check('ideas: the card opens the same dialog, filled in',
      (await dlg.locator('.i-title').inputValue()) === 'Dialog edit target' && (await dlg.locator('.i-summary').inputValue()) === 'before' && (await dlg.locator('.i-tags').inputValue()) === 't1');
    await shot(page, 'ui-ideas-02-dialog');
    // someone else writes the idea while the dialog is open: the save is a conflict, never an overwrite
    await rpc('ideas.update', { id: edited.id, patch: { summary: 'changed elsewhere' } });
    await dlg.locator('.i-title').fill('Dialog edit target (edited)');
    await dlg.locator('.i-submit').click();
    await dlg.locator('[role="alert"]').waitFor({ timeout: 5_000 }).catch(() => {});
    check('ideas: a save after another writer is a conflict, and the typed text stays',
      /changed somewhere else/.test(await dlg.locator('[role="alert"]').innerText().catch(() => '')) && (await dlg.locator('.i-title').inputValue()) === 'Dialog edit target (edited)');
    await dlg.locator('[role="alert"] button').click();
    await page.waitForFunction(() => document.querySelector('.idea-dialog .i-summary')?.value === 'changed elsewhere', null, { timeout: 5_000 }).catch(() => {});
    check('ideas: "Load the current version" shows what the other writer saved', (await dlg.locator('.i-summary').inputValue()) === 'changed elsewhere');
    await dlg.locator('.i-title').fill('Dialog edit target (edited)');
    await dlg.locator('.i-status').selectOption('planned');
    await dlg.locator('.i-submit').click();
    await dlg.waitFor({ state: 'detached', timeout: 5_000 }).catch(() => {});
    const saved = await rpc('ideas.get', { id: edited.id });
    check("ideas: the edit is saved (title, status) and the other writer's summary survived",
      saved.title === 'Dialog edit target (edited)' && saved.status === 'planned' && saved.summary === 'changed elsewhere',
      JSON.stringify({ title: saved.title, status: saved.status, summary: saved.summary }));

    // the agent task: from the card's menu, with a focus, on any available agent
    const refCard = cardTitled('Dialog edit target (edited)');
    await refCard.waitFor({ timeout: 5_000 }).catch(() => {});
    await moreMenu(refCard, 'Refine with an agent');
    await dlg.waitFor({ timeout: 5_000 }).catch(() => {});
    check('ideas: "Refine with an agent" opens the dialog with the task on', (await dlg.locator('.refine').count()) === 1 && /Save and refine/.test(await dlg.locator('.i-submit').innerText()));
    await shot(page, 'ui-ideas-03-refine');
    await dlg.locator('.i-hint').fill('check the Files plugin first');
    const refineMark = socket.mark();
    await dlg.locator('.i-submit').click();
    await dlg.waitFor({ state: 'detached', timeout: 15_000 }).catch(() => {});
    const asked = socket.sent('ideas.refine', refineMark).at(-1)?.msg?.p;
    const answer = asked ? socket.answer(socket.sent('ideas.refine', refineMark).at(-1)) : null;
    check('ideas: refining starts a chat on the idea, on any agent, with the focus',
      asked?.id === edited.id && asked.agent === undefined && asked.hint === 'check the Files plugin first' && !!answer?.msg?.r?.sessionId,
      `${JSON.stringify(asked)} → ${JSON.stringify(answer?.msg?.r ?? answer?.msg?.e)}`);
    if (answer?.msg?.r?.sessionId) seeded.push({ session: answer.msg.r.sessionId });
    await page.waitForTimeout(600);
    await openCard(refCard);
    check('ideas: the refine chat is recorded on the idea (its dialog lists it)', (await dlg.locator('.elink', { hasText: 'Refine idea: Dialog edit target' }).count()) === 1,
      (await dlg.locator('.elink').allInnerTexts()).join(' | '));
    await closeDialog();

    // tag filter (menu; the selected tag shows as a removable chip) + an external change (ideas.changed) refetches
    await page.locator('.ideas .filters button[title="Filter by tag"]').click();
    await page.locator('.np-menu .np-menu-item', { hasText: '#aiproxy' }).click();
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);
    const nTag = await cards().count();
    check('ideas: tag filter', nTag > 0 && nTag < n0, `${nTag} of ${n0}`);
    await page.locator('.ideas .tags .np-chip', { hasText: '#aiproxy' }).click();
    check('ideas: removing the tag chip clears the filter', (await cards().count()) >= n0);
    seeded.push((await rpc('ideas.add', { projectId: project.id, idea: { title: 'Added over RPC', tags: ['rpc'] } })).id);
    await page.locator('.ideas .card', { hasText: 'Added over RPC' }).waitFor({ timeout: 5_000 }).catch(() => {});
    check('ideas: refetch on ideas.changed', (await page.locator('.ideas .card', { hasText: 'Added over RPC' }).count()) > 0);

    // ---- welcome screen: ideas to work on
    for (let i = 0; i < 30 && (await page.locator('.topbar .tab').count()); i++) await page.locator('.topbar .tab .tab-close').first().click();
    // the target decides which ideas are relevant: point the start screen at the project, the way a fresh window reads it
    await page.evaluate((id) => localStorage.setItem('netpi.lastProject', JSON.stringify(id)), project.id);
    await page.reload();
    await page.locator('.welcome .target').waitFor({ timeout: 10_000 });
    const rows = page.locator('.welcome .prow');
    await rows.first().waitFor({ timeout: 5_000 }).catch(() => {});
    const wanted = (await rpc('ideas.picks', { projectId: project.id, limit: 3 })).picks ?? [];
    const shown = await rows.allInnerTexts();
    check('welcome screen: the target is the project the backlog belongs to', (await page.locator('.welcome .target').innerText()).includes(project.name), await page.locator('.welcome .target').innerText());
    check('welcome screen: the ideas of the project it targets, at most three, in their order',
      shown.length > 0 && shown.length === wanted.length && shown.length <= 3 && shown.every((t, i) => t.includes(wanted[i].title)),
      `${shown.length} of ${wanted.length}: ${shown.map((t) => t.split('\n')[0]).join(' / ')}`);
    check('welcome screen: nothing that is being worked on (in-progress, done, parked) is offered',
      !shown.some((t) => /Cache the model list|Stream compaction summaries|Semantic search/.test(t)), shown.join(' / '));
    const box = await rows.first().boundingBox();
    const recentBox = await page.locator('.welcome .recent').boundingBox().catch(() => null);
    check('welcome screen: the ideas sit above the recent sessions', !!box && (!recentBox || box.y < recentBox.y), recentBox ? `${Math.round(box.y)} vs ${Math.round(recentBox.y)}` : 'no recent sessions');
    await shot(page, 'ui-ideas-04-welcome');
    // a click starts the work: a chat in the idea's project with the idea in it
    const first = wanted[0];
    await rows.first().click();
    await page.waitForFunction(() => document.querySelector('.topbar .tab.active')?.getAttribute('data-tab'), null, { timeout: 10_000 });
    const sid = await page.locator('.topbar .tab.active').getAttribute('data-tab');
    seeded.push({ session: sid });
    await page.waitForFunction(() => document.querySelector('.notice'), null, { timeout: 5_000 }).catch(() => {});
    const started = await rpc('sessions.get', { id: sid });
    const msgs = (await rpc('sessions.messages', { id: sid })).messages ?? [];
    const notice = msgs.find((m) => m.meta?.kind === 'idea');
    check('welcome screen: a click starts a chat on the idea with the idea attached',
      started?.projectId === project.id && notice?.meta?.ideaId === first.id && (await page.locator('.composer').count()) === 1,
      `${started?.projectId} notice ${notice?.meta?.ideaId ?? 'none'}`);
    check('welcome screen: and titles the chat after the idea', started?.title === first.title, started?.title);
    // the backlog moves in another window: ideas.changed moves the offer with it
    await page.locator('.topbar .tab.active .tab-close').click();
    await page.locator('.welcome .target').waitFor({ timeout: 10_000 });
    await rpc('ideas.update', { id: first.id, patch: { status: 'done' } });
    await page.waitForFunction((gone) => ![...document.querySelectorAll('.welcome .prow')].some((r) => r.textContent.includes(gone)), first.title, { timeout: 5_000 }).catch(() => {});
    check('welcome screen: an idea finished elsewhere disappears from the offer', (await page.locator('.welcome .prow', { hasText: first.title }).count()) === 0);
    // no Ideas plugin: the start screen is exactly as it was
    await rpc('plugins.setEnabled', { id: 'netpi.ideas', enabled: false });
    try {
      await page.reload();
      await page.locator('.welcome .target').waitFor({ timeout: 10_000 });
      await page.waitForTimeout(700);
      check('welcome screen: without ideas.picks nothing is offered and nothing breaks',
        (await page.locator('.welcome .prow').count()) === 0 && (await page.locator('.welcome .recent, .keys').count()) > 0);
    } finally {
      await rpc('plugins.setEnabled', { id: 'netpi.ideas', enabled: true });
    }
  } finally {
    for (const s of seeded) {
      if (typeof s === 'string') await rpc('ideas.delete', { id: s }).catch(() => {});
      else if (s?.session) await rpc('sessions.delete', { id: s.session }).catch(() => {});
    }
    await rpc('sessions.delete', { id: chat.id }).catch(() => {});
    await rpc('projects.delete', { id: project.id }).catch(() => {});
  }
}, { viewport: { width: 1600, height: 1000 }, freshTabs: true, tap: true });
