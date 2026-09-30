# DGX / Linux server setup

## Requirements

- Python 3.8+ (tested on 3.8). No GPU required for the synthetic prototype.
- One open inbound TCP port (default 8000).

## Install & run

```bash
cd brain-streaming/server
python3 -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt

python app.py --host 0.0.0.0 --port 8000 --chunk-delay-ms 120
```

CLI flags:

| Flag | Default | Purpose |
|------|---------|---------|
| `--host` | `0.0.0.0` | bind address (`0.0.0.0` = reachable from other machines) |
| `--port` | `8000` | HTTP port |
| `--chunk-delay-ms` | `120` | artificial per-chunk latency; makes incremental streaming visible. `0` = full speed |

The client is served at `/`, so open `http://<DGX_IP>:8000/` from any browser on
the network. Everything (brain generation, chunking, LOD) is built once at
startup and cached in memory.

## Firewall

```bash
# Ubuntu / ufw
sudo ufw allow 8000/tcp
# RHEL / firewalld
sudo firewall-cmd --add-port=8000/tcp --permanent && sudo firewall-cmd --reload
```

Find the DGX IP the client should use:

```bash
hostname -I | awk '{print $1}'
```

## Run as a background service (optional)

```bash
nohup python app.py --host 0.0.0.0 --port 8000 --chunk-delay-ms 120 > brain.log 2>&1 &
```

Or with multiple workers (stateless, so it scales for many clients):

```bash
uvicorn app:app --host 0.0.0.0 --port 8000 --workers 4
# note: --chunk-delay-ms is an app.py CLI flag; with bare uvicorn the delay
# defaults to 0. Set it by editing CHUNK_DELAY_MS or use the app.py launcher.
```

## Optional: Docker (not required)

`server/Dockerfile`:

```dockerfile
FROM python:3.11-slim
WORKDIR /srv
COPY server/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY server /srv/server
COPY client /srv/client
WORKDIR /srv/server
EXPOSE 8000
ENTRYPOINT ["python", "app.py", "--host", "0.0.0.0", "--port", "8000"]
```

```bash
cd brain-streaming
docker build -t brain-server -f server/Dockerfile .
docker run --rm -p 8000:8000 brain-server --chunk-delay-ms 120
```

## Verifying

```bash
curl http://localhost:8000/api/health
curl http://localhost:8000/api/brain/info
```

Expected: `chunk_count: 8`, `lods: [0,1,2]`.
