from starlette.applications import Starlette
from starlette.responses import PlainTextResponse
from starlette.routing import Route


async def hello(request):
    return PlainTextResponse("Hello from Starlette")


app = Starlette(routes=[Route("/", hello)])
