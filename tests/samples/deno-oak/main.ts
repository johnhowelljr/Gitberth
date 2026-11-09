import { Application, Router } from "@oak/oak";

const router = new Router();
router.get("/", (ctx) => {
  ctx.response.body = "Hello from Oak";
});

const app = new Application();
app.use(router.routes());
app.use(router.allowedMethods());

await app.listen({ hostname: "0.0.0.0", port: 8080 });
