const fastify = require("fastify")({ logger: true });

fastify.get("/", async () => "Hello from Fastify");

// Without a host, Fastify only listens on localhost, which nothing outside the container can reach.
fastify.listen({ port: Number(process.env.PORT) || 3000, host: "0.0.0.0" }).catch((err) => {
  fastify.log.error(err);
  process.exit(1);
});
