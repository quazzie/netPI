// The popup: share the current tab with a NetPI chat (optionally with a message to its agent), or stop sharing it.
const $ = (id) => document.getElementById(id);

function ask(msg) {
  return new Promise((resolve, reject) =>
    chrome.runtime.sendMessage(msg, (r) => {
      if (chrome.runtime.lastError) return reject(new Error(chrome.runtime.lastError.message));
      if (r?.error) return reject(new Error(r.error));
      resolve(r?.result);
    }),
  );
}

function say(text, err = false) {
  $('status').textContent = text;
  $('status').className = err ? 'msg err' : 'msg';
}

async function main() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  const status = await ask({ op: 'status' }).catch(() => ({ connected: false }));
  $('dot').className = status.connected ? 'dot on' : 'dot';
  $('off').hidden = status.connected;
  $('on').hidden = !status.connected;
  if (!status.connected || !tab) return;
  $('title').textContent = tab.title || '(untitled)';
  $('url').textContent = tab.url || '';

  const mine = (status.shared ?? []).find((t) => t.tabId === tab.id);
  $('sharing').hidden = !mine;
  $('form').hidden = !!mine;
  if (mine) $('sharedWith').textContent = mine.chat ?? 'NetPI (the next chat that asks for it)';

  const chats = await ask({ op: 'chats' }).catch(() => []);
  const select = $('chat');
  select.replaceChildren();
  const add = (value, label) => {
    const o = document.createElement('option');
    o.value = value;
    o.textContent = label;
    select.appendChild(o);
  };
  add('new', 'A new chat');
  for (const c of chats ?? []) add(c.id, c.title);
  add('', 'Any chat (an agent takes it with "tabs")');
  if (chats?.length) select.value = chats[0].id;

  $('share').onclick = async () => {
    $('share').disabled = true;
    try {
      const r = await ask({ op: 'share', tabId: tab.id, url: tab.url, title: tab.title, sessionId: select.value || null, text: $('text').value });
      say(r?.chat ? `Shared with “${r.chat}”.` : 'Shared: the next chat that asks for a tab gets it.');
      setTimeout(() => window.close(), 900);
    } catch (e) {
      say(e.message, true);
      $('share').disabled = false;
    }
  };
  $('stop').onclick = async () => {
    try {
      await ask({ op: 'unshare', tabId: tab.id });
      say('Stopped sharing.');
      setTimeout(() => window.close(), 700);
    } catch (e) {
      say(e.message, true);
    }
  };
}

$('retry').onclick = async () => {
  say('Connecting…');
  await ask({ op: 'reconnect' }).catch(() => {});
  setTimeout(() => location.reload(), 600);
};

main().catch((e) => say(e.message, true));
