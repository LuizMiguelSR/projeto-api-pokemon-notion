"""JSON stdin/stdout bridge between the API and D4Vinci/Scrapling."""
import contextlib
import json
import sys
from http.cookies import SimpleCookie


def fetch(request):
    from scrapling.fetchers import StealthyFetcher

    def capture_runtime(page):
        page.evaluate("""() => {
            const data = typeof cards_editions !== 'undefined' ? cards_editions : [];
            const node = document.createElement('script');
            node.id = 'liga-pokemon-runtime-data';
            node.type = 'application/json';
            node.textContent = JSON.stringify({cards_editions: data});
            document.querySelector('#liga-pokemon-runtime-data')?.remove();
            (document.body || document.documentElement).appendChild(node);
        }""", isolated_context=False)

    cookies = SimpleCookie()
    cookies.load(request.get("cookie") or "")
    response = StealthyFetcher.fetch(
        request["url"],
        headless=True,
        solve_cloudflare=True,
        timeout=request["timeoutSeconds"] * 1000,
        network_idle=True,
        google_search=False,
        locale="pt-BR",
        extra_headers={"Accept-Language": request["acceptLanguage"]},
        cookies=[{"name": key, "value": value.value, "url": request["baseUrl"]}
                 for key, value in cookies.items()],
        page_action=capture_runtime,
    )
    return {"statusCode": response.status, "html": response.html_content}


if __name__ == "__main__":
    try:
        request = json.load(sys.stdin)
        # Dependencies may log to stdout; reserve it exclusively for the JSON reply.
        with contextlib.redirect_stdout(sys.stderr):
            result = fetch(request)
        print(json.dumps(result, ensure_ascii=True))
    except Exception as exc:
        print(f"Scrapling failed: {exc}", file=sys.stderr)
        sys.exit(1)
