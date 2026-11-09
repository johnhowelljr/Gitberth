// A route with no content type behind it, open to everyone: GET /api/hello.
module.exports = {
  routes: [
    {
      method: 'GET',
      path: '/hello',
      handler: 'api::hello.hello.index',
      config: {
        auth: false,
      },
    },
  ],
};
