# The scenario runner: pytest and Playwright (its image's browsers, so the package's version must match the tag), plus
# the docker CLI to look at the rig's engine through its socket.
FROM mcr.microsoft.com/playwright/python:v1.58.0-noble
COPY --from=docker:29-cli /usr/local/bin/docker /usr/local/bin/docker
RUN pip install --no-cache-dir --break-system-packages playwright==1.58.0 pytest==8.3.5
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /e2e/scenarios
