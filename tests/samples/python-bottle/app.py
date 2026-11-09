from bottle import Bottle

app = Bottle()


@app.get("/")
def hello():
    return "Hello from Bottle"
