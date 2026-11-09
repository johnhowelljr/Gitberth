const express = require("express");

const app = express();
app.get("/", (req, res) => res.send("Hello from Express"));

// The scheduled jobs in vercel.json call these, as Vercel's crons would.
app.get("/api/cleanup", (req, res) => res.json({ removed: 0 }));
app.get("/api/report", (req, res) => res.json({ ok: true }));

const port = process.env.PORT || 3000;
app.listen(port, () => console.log(`Listening on port ${port}`));
