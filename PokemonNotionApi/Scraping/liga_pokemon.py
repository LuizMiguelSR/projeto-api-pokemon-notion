"""JSON stdin/stdout bridge between the API and D4Vinci/Scrapling."""
import contextlib
import json
import sys
from http.cookies import SimpleCookie


def wait_for_card_data(page, timeout_ms):
    """Allow delayed scripts and intermediate challenge pages to finish loading."""
    from patchright.sync_api import Error

    for attempt in range(max(1, timeout_ms // 500)):
        try:
            ready = page.evaluate("""() =>
                typeof cards_editions !== 'undefined' &&
                Array.isArray(cards_editions) &&
                cards_editions.some(edition => edition && edition.price &&
                    Object.keys(edition.price).length > 0)
            """, isolated_context=False)
            if ready:
                return True
        except Error as exc:
            # A challenge may navigate while its document is being inspected.
            if "Execution context was destroyed" not in str(exc):
                raise
        page.wait_for_timeout(500)
    print("Card prices did not become available within the page wait limit.",
          file=sys.stderr)
    return False


def fetch(request):
    from scrapling.fetchers import StealthyFetcher

    def capture_runtime(page):
        wait_for_card_data(page, min(20000, request["timeoutSeconds"] * 500))
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
