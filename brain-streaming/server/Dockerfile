# Optional deployment. NOT required to run the prototype.
# Build from the brain-streaming/ directory:
#   docker build -t brain-server -f server/Dockerfile .
#   docker run --rm -p 8000:8000 brain-server --chunk-delay-ms 120
FROM python:3.11-slim
WORKDIR /srv
COPY server/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY server /srv/server
COPY client /srv/client
WORKDIR /srv/server
EXPOSE 8000
ENTRYPOINT ["python", "app.py", "--host", "0.0.0.0", "--port", "8000"]
