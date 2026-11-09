import { Elysia } from "elysia";

new Elysia()
  .get("/", () => "Hello from Elysia")
  .listen(process.env.PORT ?? 3000);
