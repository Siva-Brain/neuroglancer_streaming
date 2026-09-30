# Multi-block DGX Brain Streaming server (real HTTP Zarr edition), CPU image.
# Runs app_zarr.py, which serves all 5 block-cut slabs (580/584/585/586/587)
# and the WebGL client at "/". GPU is optional -> this image runs --no-gpu and
# falls back to NumPy cleanly. For A100/CuPy acceleration use Dockerfile.gpu.
#
# Build from the brain-streaming/ directory (build context = brain-streaming/):
#   docker build -t brain-streaming -f server/Dockerfile .
#   docker run --rm -p 8010:8010 brain-streaming
#
# Point at your own blocks (repeat --zarr) or tune anything by appending args:
#   docker run --rm -p 8010:8010 brain-streaming \
#       --zarr http://host:8056/zarr_files/580_ALL_3d.zarr \
#       --zarr http://host:8056/zarr_files/584_ALL_3d.zarr --min-level 4
FROM python:3.11-slim

WORKDIR /srv/server

# deps first for layer caching
COPY server/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY server /srv/server
COPY client /srv/client

EXPOSE 8010

# host/port are fixed in the entrypoint so overriding CMD keeps the server bound;
# CMD holds the tunables (override by appending flags to `docker run`).
ENTRYPOINT ["python", "app_zarr.py", "--host", "0.0.0.0", "--port", "8010"]
CMD ["--no-gpu", "--min-level", "3"]
