from litestar import Litestar, get


@get("/")
async def hello() -> str:
    return "Hello from Litestar"


app = Litestar([hello])
