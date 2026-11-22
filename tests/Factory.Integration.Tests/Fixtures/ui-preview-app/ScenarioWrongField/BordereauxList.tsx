import { useState } from "react";

// A page that sends the wrong field name: pattern for match.
export function BordereauxList() {
  const [open, setOpen] = useState(false);
  const [match, setMatch] = useState("");
  const [tag, setTag] = useState("Priority");
  const apply = () => { void fetch("/orders/tags", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ pattern: match, tag }) }); };
  return (
    <div>
      <button onClick={() => setOpen(true)}>Tag orders</button>
      {open && (
        <form onSubmit={(e) => e.preventDefault()}>
          <label>Match <input type="text" value={match} onChange={(e) => setMatch(e.target.value)} /></label>
          <label>Tag <select value={tag} onChange={(e) => setTag(e.target.value)}><option>Priority</option><option>Hold</option></select></label>
          <button onClick={apply}>Apply</button>
        </form>
      )}
    </div>
  );
}
