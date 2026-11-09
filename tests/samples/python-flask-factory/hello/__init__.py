from flask import Flask


def create_app():
    app = Flask(__name__)

    @app.get("/")
    def hello():
        return "Hello from a Flask app factory"

    return app
