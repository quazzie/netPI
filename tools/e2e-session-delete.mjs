// E2E: session.create -> session.delete -> session.list (verifies deletion).
const WS = new WebSocket("ws://127.0.0.1:5173/ws");
let reqId = 0;
const pending = new Map();
let created = null;
let listAfter = null;

function request(type, payload) {
  const id = `t${reqId++}`;
  WS.send(JSON.stringify({ type, requestId: id, payload }));
  return new Promise((res, rej) => {
    pending.set(id, { res, rej });
    setTimeout(() => { pending.delete(id); rej(new Error(`timeout ${type}`)); }, 10000);
  });
}

WS.onmessage = (ev) => {
  const m = JSON.parse(ev.data);
  if (m.requestId && pending.has(m.requestId)) {
    const p = pending.get(m.requestId);
    pending.delete(m.requestId);
    if (m.type === "error") p.rej(new Error(JSON.stringify(m.payload)));
    else p.res(m.payload);
    return;
  }
  if (m.type === "session.created") created = m.payload;
  if (m.type === "session.list") listAfter = m.payload;
};

WS.onopen = async () => {
  try {
    // let bootstrap settle
    await new Promise((r) => setTimeout(r, 1500));

    await request("session.create", { workspace: "C:\\tmp\\netpi-e2e" });
    if (!created) throw new Error("session.created event never arrived");
    console.log("created:", created.id, created.title, created.workspace);

    await request("session.delete", { sessionId: created.id });

    // ask for a fresh listing (the server also broadcasts session.deleted)
    listAfter = null;
    await request("session.list", {});
    await new Promise((r) => setTimeout(r, 500));
    if (!listAfter) throw new Error("no post-delete session.list received");
    const still = listAfter.sessions.filter((s) => s.id === created.id);
    if (still.length) throw new Error("session still in list after delete: " + JSON.stringify(still));
    console.log("PASS: deleted session absent from session.list");

    // error path: deleting an unknown id should still ack (no-op) not crash
    await request("session.delete", { sessionId: "no-such-id" });
    console.log("PASS: unknown-id delete acks without error");

    WS.close();
    process.exit(0);
  } catch (e) {
    console.error("FAIL:", e.message);
    process.exit(1);
  }
};
WS.onerror = (e) => { console.error("ws error", e.message ?? ""); process.exit(1); };
