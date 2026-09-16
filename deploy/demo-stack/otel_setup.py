"""OpenTelemetry wiring for the demo service (v0.1.8).

Two goals:
1. Server traces: every HTTP request + custom spans (model load, bootstrap
   stages, chunked extraction, model-file serving) exported over OTLP/HTTP
   to the local collector (:4318) → OpenObserve.
2. CLIENT error telemetry: the pages POST failures (fetch "Load failed"
   included) to /telemetry; we convert them to spans so device-specific
   breakage (iOS content blockers, OOM aborts) is diagnosable from the
   observability stack instead of a one-line Safari error.

Fails soft: if the collector is down or OTel is absent, the service runs
unchanged (demo first, observability second).
"""

from __future__ import annotations

import os

OTEL_ENABLED = os.environ.get("OTEL_ENABLED", "1") == "1"
OTEL_ENDPOINT = os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4318")
SERVICE_NAME = "gliformer-demo"
SERVICE_VERSION = "0.1.8"

_tracer = None
_provider = None


def init_otel(app):
    """Instrument the FastAPI app; returns a tracer (or a no-op)."""
    global _tracer, _provider
    if not OTEL_ENABLED:
        return _noop_tracer()
    try:
        from fastapi import FastAPI  # noqa: F401
        from opentelemetry import trace
        from opentelemetry.exporter.otlp.proto.http.trace_exporter import (
            OTLPSpanExporter,
        )
        from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor
        from opentelemetry.sdk.resources import Resource
        from opentelemetry.sdk.trace import TracerProvider
        from opentelemetry.sdk.trace.export import BatchSpanProcessor

        resource = Resource.create(
            {
                "service.name": SERVICE_NAME,
                "service.version": SERVICE_VERSION,
                "host.name": os.uname().nodename,
            }
        )
        _provider = TracerProvider(resource=resource)
        _provider.add_span_processor(
            BatchSpanProcessor(
                OTLPSpanExporter(endpoint=OTEL_ENDPOINT + "/v1/traces")
            )
        )
        trace.set_tracer_provider(_provider)
        FastAPIInstrumentor.instrument_app(app, tracer_provider=_provider)
        _tracer = trace.get_tracer(SERVICE_NAME)
        return _tracer
    except Exception as e:  # observability must never kill the demo
        print(f"[otel] disabled ({e.__class__.__name__}: {e})")
        return _noop_tracer()


class _NoopSpan:
    def set_attribute(self, *a): return self
    def set_status(self, *a): return self
    def record_exception(self, *a): return self
    def end(self): pass
    def __enter__(self): return self
    def __exit__(self, *a): return False


class _NoopTracer:
    def start_as_current_span(self, *a, **k): return _NoopSpan()


def _noop_tracer():
    return _NoopTracer()


def tracer():
    return _tracer or _noop_tracer()


def client_event(event: str, attrs: dict) -> None:
    """Client-side report (beacon) → span, so device failures are queryable."""
    t = tracer()
    with t.start_as_current_span("gliformer.client_event") as span:
        span.set_attribute("event", event)
        for k, v in attrs.items():
            span.set_attribute(f"client.{k}", str(v)[:500])
