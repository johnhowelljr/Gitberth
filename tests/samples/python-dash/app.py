from dash import Dash, html

app = Dash(__name__)
app.layout = html.Div("Hello from Dash")
server = app.server
