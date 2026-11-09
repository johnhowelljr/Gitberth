# Sample projects

The smallest project of each kind Gitberth builds, kept as a regression suite.

- **On every test run.** `dotnet test` detects each sample (`SampleTests`) and checks the result against its `sample.json`: the kind, the title and the container port.
- **On Docker.** `check-remote.ps1` builds each sample with the Dockerfile Gitberth generates for it, starts it, and checks that it answers. Run it after changing detection or a template:

  ```powershell
  ./tests/samples/check-remote.ps1 -Target root@<docker host> -Key <ssh key>
  ./tests/samples/check-remote.ps1 -Target root@<docker host> -Only python-fastapi, node-express
  ```

  On the Docker host itself, `check-on-docker.sh <samples> <generated> [name …]` does the same with Dockerfiles you've already generated (`GITBERTH_SAMPLES_OUT=<dir> dotnet test --filter Writes_each_samples_dockerfile_for_the_docker_check`). Containers only listen on 127.0.0.1, and every image and container is removed afterwards.
- **To look at them.** With `-Keep` (`--keep` on the host), each sample that answers stays running as `sample-<name>` on the host's address, from port 9101 up, with an index of them on port 9100. A new run replaces the samples kept from the last one. `-Clean` (`--clean`) removes them and their images.

## sample.json

| Field | Meaning |
|---|---|
| `kind` | The `ProjectKind` detection must give, like `Python` or `NextJs`. |
| `title` | Text the detected title must contain, like `FastAPI`. |
| `port` | The container port detection must give; `null` for a worker. |
| `appDir` | The app's folder, for a sample laid out like a monorepo. Optional. |
| `path` | What the Docker check requests. Default `/`. |
| `expect` | Text the answer must contain. |
| `env` | Variables the container gets in the Docker check. Optional. |
| `worker` | `true` for an app with no HTTP: the Docker check only makes sure it keeps running. |

## Adding one

- **One sample for each framework or layout Gitberth supports**, named `<language>-<framework>` (`python-fastapi`, `node-express`).
- **As small as the framework allows.** One route that answers with a sentence naming the framework ("Hello from FastAPI"), and the fewest files and dependencies that still make it a real project of that kind.
- **Versions are pinned to a major** (`^16.0.0`, `fastapi>=0.115,<1`), so a new upstream release can't quietly change what the suite tests. Leave out lockfiles unless the sample is about one.
- **It's built from its own folder**, as Gitberth would build it from a repo. `appDir` points at the app when the sample is laid out like a monorepo.
