# A fake cloudflared (see fake_cloudflared.py). Its image name has "cloudflared" in it and it runs as `cloudflared
# --no-autoupdate tunnel run --token <token>`, as the real image does, so Gitberth finds it the same way. Built from
# standins/, since it reads ingress the way the stand-in does (ingress.py):
#   docker build -f standins/cloudflared/Dockerfile standins
FROM python:3.12-alpine
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1
COPY ingress.py /usr/local/lib/gbe2e/ingress.py
COPY cloudflared/fake_cloudflared.py /usr/local/bin/cloudflared
RUN chmod +x /usr/local/bin/cloudflared
EXPOSE 7844
ENTRYPOINT ["cloudflared", "--no-autoupdate"]
CMD ["version"]
