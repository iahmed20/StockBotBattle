# Image that runs one submitted strategy. Build from this folder:
#   docker build -t stock-bot-sandbox sandbox/
# The API starts one container per submission with no network, a read-only filesystem,
# and CPU, memory, and process limits (see SandboxOptions).
FROM python:3.12-slim-bookworm

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1

COPY requirements.txt /tmp/requirements.txt
RUN pip install -r /tmp/requirements.txt \
 && rm /tmp/requirements.txt \
 && pip uninstall -y pip setuptools wheel

COPY runner.py datamodel.py /app/

RUN useradd --uid 10001 --no-create-home --shell /usr/sbin/nologin runner

USER 10001
WORKDIR /app
ENTRYPOINT ["python", "/app/runner.py"]
