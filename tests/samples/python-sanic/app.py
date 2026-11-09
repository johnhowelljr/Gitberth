from sanic import Sanic
from sanic.response import text

app = Sanic("hello")


@app.get("/")
async def hello(request):
    return text("Hello from Sanic")
