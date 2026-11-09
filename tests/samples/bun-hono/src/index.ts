import { Hono } from "hono";

const app = new Hono();
app.get("/", (c) => c.text("Hello from Hono on Bun"));

// Bun serves a default export that has a fetch method, on PORT (3000 when it isn't set).
export default app;
