import gradio as gr

with gr.Blocks(title="Hello from Gradio") as demo:
    gr.Markdown("Hello from Gradio")

demo.launch()
