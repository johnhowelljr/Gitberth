# The GitHub and Cloudflare stand-ins: Python's standard library, the git CLI (http-backend serves the repos) and openssl
# (the cert maker). Built from tests/e2e, so the app templates come along: docker build -f standins/Dockerfile tests/e2e
FROM python:3.12-alpine
RUN apk add --no-cache git git-daemon openssl \
    && git config --system uploadpack.allowAnySHA1InWant true \
    && git config --system uploadpack.allowReachableSHA1InWant true \
    && git config --system uploadpack.allowTipSHA1InWant true \
    && git config --system init.defaultBranch main \
    && git config --system advice.detachedHead false
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 STANDIN_TEMPLATES=/app/apps
COPY standins/*.py /app/standins/
COPY apps/ /app/apps/
EXPOSE 443 8099
CMD ["python3", "/app/standins/standin.py"]
