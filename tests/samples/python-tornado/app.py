import asyncio
import os

import tornado.web


class Hello(tornado.web.RequestHandler):
    def get(self):
        self.write("Hello from Tornado")


async def main():
    app = tornado.web.Application([(r"/", Hello)])
    app.listen(int(os.environ.get("PORT", 8888)), xheaders=True)
    await asyncio.Event().wait()


if __name__ == "__main__":
    asyncio.run(main())
