const fs = require('fs');
const puppeteer = require('puppeteer');

async function main() {
  const [url, userAgent, acceptLanguage, timeoutArg, headlessArg, cookie] = process.argv.slice(2);
  if (!url) {
    console.error('Usage: node liga-pokemon-puppeteer.js <url> [userAgent] [acceptLanguage] [timeoutMs] [headless]');
    process.exit(2);
  }

  const timeout = Number.parseInt(timeoutArg || '45000', 10);
  const headless = (headlessArg || 'true').toLowerCase() !== 'false';
  const executablePath = resolveBrowserExecutablePath();
  const launchOptions = {
    headless,
    args: [
      '--disable-dev-shm-usage',
      '--disable-gpu',
      '--disable-setuid-sandbox',
      '--no-sandbox'
    ]
  };

  if (executablePath) {
    launchOptions.executablePath = executablePath;
  }

  const browser = await puppeteer.launch(launchOptions);

  try {
    const page = await browser.newPage();
    if (userAgent) {
      await page.setUserAgent(userAgent);
    }

    const headers = {};
    if (acceptLanguage) {
      headers['accept-language'] = acceptLanguage;
    }

    if (cookie) {
      const parsedCookies = parseCookies(cookie, url);
      if (parsedCookies.length > 0) {
        await page.setCookie(...parsedCookies);
      } else {
        headers.cookie = cookie;
      }
    }

    if (Object.keys(headers).length > 0) {
      await page.setExtraHTTPHeaders(headers);
    }

    await page.goto(url, {
      waitUntil: 'domcontentloaded',
      timeout
    });

    await page.waitForFunction(
      () => Boolean(window.cards_editions) ||
        (
          !document.title.toLowerCase().includes('just a moment') &&
          !document.title.toLowerCase().includes('um momento') &&
          !document.querySelector('script[src*="cdn-cgi/challenge-platform"]') &&
          !document.querySelector('input[name="cf-turnstile-response"]')
        ),
      { timeout }
    ).catch(() => {});

    const runtimeData = await page.evaluate(() => {
      const globals = {};
      for (const key of Object.keys(window)) {
        const lowerKey = key.toLowerCase();
        if (!lowerKey.includes('card') && !lowerKey.includes('edition') && !lowerKey.includes('price')) {
          continue;
        }

        try {
          const value = window[key];
          if (value && typeof value !== 'function') {
            globals[key] = JSON.parse(JSON.stringify(value));
          }
        } catch {
          // Ignore cross-origin or protected globals.
        }
      }

      return {
        cards_editions: window.cards_editions || null,
        globals
      };
    });

    let html = await page.content();
    if (runtimeData.cards_editions) {
      html += `\n<script>var cards_editions = ${JSON.stringify(runtimeData.cards_editions)};</script>`;
    }

    html += `\n<script type="application/json" id="liga-pokemon-runtime-data">${JSON.stringify(runtimeData)}</script>`;
    process.stdout.write(html);
  } finally {
    await browser.close();
  }
}

function parseCookies(cookieHeader, url) {
  const cleanCookieHeader = cookieHeader.replace(/^cookie:\s*/i, '').trim();
  const parsedUrl = new URL(url);

  return cleanCookieHeader
    .split(';')
    .map(cookie => cookie.trim())
    .filter(Boolean)
    .map(cookie => {
      const equalsIndex = cookie.indexOf('=');
      if (equalsIndex <= 0) {
        return null;
      }

      const name = cookie.slice(0, equalsIndex).trim();
      const value = cookie.slice(equalsIndex + 1).trim();
      if (!name || /^(path|domain|expires|max-age|secure|httponly|samesite)$/i.test(name)) {
        return null;
      }

      return {
        name,
        value,
        url: parsedUrl.origin
      };
    })
    .filter(Boolean);
}

function resolveBrowserExecutablePath() {
  const candidates = [
    process.env.PUPPETEER_EXECUTABLE_PATH,
    process.env.CHROME_BIN,
    '/usr/bin/chromium',
    '/usr/bin/chromium-browser',
    '/usr/bin/google-chrome',
    '/usr/bin/google-chrome-stable',
    '/opt/google/chrome/chrome'
  ].filter(Boolean);

  return candidates.find(candidate => fs.existsSync(candidate));
}

main().catch(error => {
  console.error(error && error.stack ? error.stack : String(error));
  process.exit(1);
});
