"use strict";

const Hapi = require("@hapi/hapi");

const init = async () => {
  // hapi's guide uses host 'localhost', which would only answer inside the container.
  const server = Hapi.server({ port: Number(process.env.PORT) || 3000, host: "0.0.0.0" });

  server.route({
    method: "GET",
    path: "/",
    handler: () => "Hello from hapi",
  });

  await server.start();
  console.log("Server running on %s", server.info.uri);
};

process.on("unhandledRejection", (err) => {
  console.log(err);
  process.exit(1);
});

init();
