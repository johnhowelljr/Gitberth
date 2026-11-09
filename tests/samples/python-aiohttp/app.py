import os

from aiohttp import web


async def hello(request):
    return web.Response(text="Hello from aiohttp")


app = web.Application()
app.add_routes([web.get("/", hello)])

if __name__ == "__main__":
    web.run_app(app, port=int(os.environ.get("PORT", 8080)))
