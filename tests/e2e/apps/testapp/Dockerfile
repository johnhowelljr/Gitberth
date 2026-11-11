# The rig's test app: a tiny Python server that says what it was built and started with. Gitberth fills in the marker
# line with the build's variables (and mounts its build-time secrets into the RUN below it).
FROM python:3.12-alpine
WORKDIR /app
COPY . .
# gitberth:build-args
RUN python3 /app/build_info.py > /app/build.json
EXPOSE 8080
VOLUME /data
CMD ["python3", "-u", "/app/server.py"]
