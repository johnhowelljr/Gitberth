import { serve } from "@hono/node-server";
import { Hono } from "hono";

const app = new Hono();
app.get("/", (c) => c.text("Hello from Hono"));

// As in create-hono's Node template: a fixed port, which Gitberth reads from here.
serve({ fetch: app.fetch, port: 3000 });
