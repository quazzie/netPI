import json, os, sqlite3, sys

DB = os.path.expanduser(os.path.join("~", ".netpi", "netpi.db"))
WORDS = 5

def title_from(text: str) -> str:
    words = text.split()
    if not words:
        return ""
    return " ".join(words[:WORDS])

def first_user_prompt(row_payloads):
    for raw in row_payloads:
        try:
            env = json.loads(raw)
        except Exception:
            continue
        if env.get("role") not in (0, "user", "User"):
            continue
        parts = env.get("parts") or []
        for part in parts:
            if part.get("kind") == "text" and part.get("text"):
                return part["text"]
        texts = [p.get("text") for p in parts if p.get("text")]
        if texts:
            return " ".join(texts)
    return None

conn = sqlite3.connect(DB)
conn.row_factory = sqlite3.Row
apply = "--apply" in sys.argv

sessions = conn.execute("SELECT id, title, updated_at FROM sessions ORDER BY updated_at DESC").fetchall()
print(f"{len(sessions)} sessions\n")

changes = []
for s in sessions:
    entries = conn.execute(
        "SELECT payload_json FROM session_entries WHERE session_id=? AND entry_type='Message' ORDER BY seq",
        (s["id"],),
    ).fetchall()
    prompt = first_user_prompt([e["payload_json"] for e in entries])
    new = title_from(prompt) if prompt else ""
    old = s["title"]
    mark = "" if (new and new != old) else "  (no change)"
    print(f"  old: {old!r}\n  new: {new!r}{mark}")
    if new and new != old:
        changes.append((new, s["id"]))

print(f"\n{len(changes)} renames pending.")
if apply:
    for title, sid in changes:
        # keep updated_at as-is so session ordering doesn't shuffle
        conn.execute("UPDATE sessions SET title=? WHERE id=?", (title, sid))
    conn.commit()
    print("applied.")
conn.close()
