# Send with GitHub: owner setup

"Send with GitHub" on ds5untethered.com/translate works like this:

1. The volunteer signs in with GitHub in a pop-up. GitHub only confirms who they are: the sign-in asks for no
   permissions, and the consent screen shows public data only.
2. The page sends the translation to a small Cloudflare Worker (the files in this folder).
3. The Worker asks GitHub who the volunteer is, then deletes the sign-in at once (the whole grant, not only the
   token), so GitHub shows its consent screen again at every sign-in.
4. The project's GitHub App bot, `ds5-untethered-translate[bot]`, opens the translation issue. The Worker builds the
   whole issue itself; nothing the volunteer typed is copied into it. The issue has a line
   `**Sender:** login (id 123, verified)` (no `@`, so nobody gets a notification).
5. The intake workflow turns the issue into a pull request. The volunteer is the author of the commit, so they
   appear in the repository's Contributors list, unless they asked not to be listed.

You need about 45 minutes, your GitHub account, the Cloudflare account that serves ds5untethered.com, Node.js (for
`npx wrangler`) and a second GitHub account for the final test. Do the steps in order. Every command runs in
PowerShell, in this folder (`tools\i18n\translate-worker`).

Until the site's `config.js` has an OAuth Client ID, the page hides "Send with GitHub" and offers the other two ways.

## 1. Create the OAuth App (the sign-in)

1. On GitHub, open **Settings > Developer settings > OAuth Apps > New OAuth App**.
2. Fill in:
   - **Application name:** `DS5 Untethered Translate`. Volunteers see this name when they sign in.
   - **Homepage URL:** `https://ds5untethered.com/translate/`
   - **Authorization callback URL:** `https://ds5untethered.com/` while you test the preview copy of the site, then
     `https://ds5untethered.com/translate/` once the live page is published (see "The callback URL" below).
   - **Enable Device Flow:** leave it off.
3. Press **Register application**.
4. On the next page, copy the **Client ID** into a note. It is public.
5. Press **Generate a new client secret**. GitHub shows the secret only once. Keep this page open; you paste the secret
   into the Worker in step 4. Never save it in a file, the repository, a chat or an e-mail.

There is no scope setting on this page. The page's sign-in link asks for no scopes, so the volunteer grants nothing.

### The callback URL

An OAuth App has only one callback URL. GitHub accepts any `redirect_uri` that is the same address or a path
below it (GitHub docs, "Authorizing OAuth apps", section "Redirect URLs"). The page uses one of two callback pages:

- live: `https://ds5untethered.com/translate/oauth-callback.html`;
- preview: `https://ds5untethered.com/preview-e31a5521/translate/oauth-callback.html`.

The Worker accepts only these two as the page's `redirect_uri` and sends it to GitHub with the code. The preview page
is not below `/translate/`; the only callback URL that covers both is the site root. So:

1. **While you test the preview:** set the callback URL to `https://ds5untethered.com/`. One App, one Client ID, one
   Worker serve both copies.
2. **Once the live page is published:** change it to `https://ds5untethered.com/translate/` (OAuth App page >
   **Authorization callback URL** > **Update application**). Sign-in from the preview then stops working: GitHub
   refuses its `redirect_uri`. To test a later preview, either set the root again for the test and narrow it back
   afterwards, or register a temporary second OAuth App with the preview's callback and use it with a second copy of
   the Worker (its own name, route and secrets). Or leave Send with GitHub untested on the preview; the other two ways
   still work there.

Why narrow it: PKCE protects only the sign-ins the page itself starts. Anyone can start a sign-in with their own PKCE
verifier and a `redirect_uri` pointing at any page under the callback URL. If such a page leaks its address (a
`Referer` header, a third-party script, an open redirect), that person gets a code for the victim's GitHub account,
and with their own verifier the Worker would accept it and send in the victim's name. Three things stop that:

- the Worker accepts only the two callback pages above, and GitHub refuses a code at the exchange when the
  `redirect_uri` differs from the one the sign-in started with;
- the Worker deletes the grant after every use, so the victim always sees GitHub's consent screen (with
  "DS5 Untethered Translate" on it) and is never signed in silently;
- a callback URL of `/translate/` leaves no other page of the site in reach at all.

The trade-off: the root callback keeps the preview testable with no extra setup, but leaves the whole site in reach
of a crafted sign-in link; `/translate/` closes that, and the preview needs the extra steps above to be tested.

## 2. Prepare the GitHub App (the bot)

The App `ds5-untethered-translate` already exists. It now only opens issues as its bot; volunteers no longer sign in
to it.

1. Open **Settings > Developer settings > GitHub Apps > ds5-untethered-translate > Edit**.
2. On **General**, copy the **App ID** (a number near the top) into your note. It is public. It is not the Client ID.
3. **Permissions & events:** **Repository permissions > Issues: Read and write**. Everything else stays at "No access"
   (GitHub adds **Metadata: Read-only** itself; that is normal). No events. **Webhook > Active** stays off.
   If you change a permission here, GitHub asks you to accept it on the installation: open the e-mail or
   **Settings > Applications > Installed GitHub Apps > ds5-untethered-translate** and accept.
4. **Install App:** the App must be installed on your account with **Only select repositories > ds5-untethered**.
   Check the gear icon next to your account if it is already installed.
5. Remove what is no longer used (all on **General**):
   - under **Client secrets**, press **Delete** next to every client secret;
   - under **Identifying and authorizing users**, clear the **Callback URL** and leave "Request user authorization
     (OAuth) during installation" off.
6. Optional but recommended: **Advanced > Make private** ("Only on this account"). Volunteers no longer authorize this
   App, so it does not need to be public.
7. **Private keys > Generate a private key.** Your browser downloads a file like
   `ds5-untethered-translate.2026-11-26.private-key.pem` into Downloads. It goes into the Worker in step 4, and then you
   delete it. Never move it into the repository folder.

Do not rename the App. The intake workflow trusts exactly the bot login `ds5-untethered-translate[bot]`
(`tools/i18n/translation-check/intake.js` and `.github/workflows/translation-intake.yml`).

## 3. Fill in `wrangler.toml`

Open `wrangler.toml` in this folder and replace:

- `REPLACE_WITH_OAUTH_CLIENT_ID` with the OAuth App's Client ID (step 1);
- `REPLACE_WITH_APP_ID` with the GitHub App's App ID (step 2), for example `"123456"`.

Leave `REPO = "Midzer00/ds5-untethered"` and `DEV = "false"`.

Then create the store for the hourly limit (5 sends per volunteer per hour). If the store is configured but fails,
the Worker refuses sends with `rate_limited` until it works again:

```
npx wrangler login
npx wrangler kv namespace create SUBMIT_LOG
```

The second command prints an `id = "..."`. Replace `REPLACE_WITH_KV_NAMESPACE_ID` with it. The store keeps only the
volunteer's GitHub user id and the times of their sends, and each entry deletes itself after one hour.

Do not commit these filled-in values back into the repository's copy unless you want them public. The Client ID and
App ID are public values, so committing them is safe, but the repository copy keeps placeholders by default.

## 4. Put the secrets into the Worker and deploy

1. The OAuth App's client secret (step 1). Paste it when asked:

   ```
   npx wrangler secret put OAUTH_CLIENT_SECRET
   ```

2. The GitHub App's private key (step 2). A key file has several lines, so pipe the file instead of pasting it. Use
   your own file name:

   ```
   Get-Content -Raw "$env:USERPROFILE\Downloads\ds5-untethered-translate.2026-11-26.private-key.pem" | npx wrangler secret put APP_PRIVATE_KEY
   ```

   GitHub gives the key in the PKCS#1 format (`BEGIN RSA PRIVATE KEY`). The Worker converts it itself. A PKCS#8 key
   (`BEGIN PRIVATE KEY`) also works.

3. Remove the secret of the old setup, if the Worker still has it:

   ```
   npx wrangler secret delete GITHUB_CLIENT_SECRET
   ```

4. Check that exactly the two secrets are there:

   ```
   npx wrangler secret list
   ```

   The list should name `APP_PRIVATE_KEY` and `OAUTH_CLIENT_SECRET`, and nothing else.

5. Deploy:

   ```
   npx wrangler deploy
   ```

   The Worker answers at `https://translate-auth.ds5untethered.com`. The old `/token` and `/revoke` addresses are
   gone, so publish the new translation page (it calls `/submit`) right after this step.

6. Delete the `.pem` file from Downloads and empty the Recycle Bin. GitHub keeps only the public half. If you ever
   need the key again, you generate a new one.

### Rate limits

The Worker already limits each IP address to 10 requests a minute, each volunteer to 2 sends a minute and 5 an hour,
and all volunteers together to 20 sends a minute (`GLOBAL_LIMITER`; remove its block in `wrangler.toml` to turn that
off).

All of these limits are approximate, not exact:

- Cloudflare counts the per-minute limits per location (data centre), so a volunteer or an attacker reaching several
  locations gets more.
- The hourly cap lives in Workers KV, which is eventually consistent: a write can take up to about a minute to be seen
  in other locations, and two sends at the same moment can both read the old count. A few sends more than 5 an hour
  are possible; a steady flood is not.
- When the KV store is configured but fails, the Worker refuses the send (`rate_limited`) instead of skipping the cap.

Also update the zone rule:

1. Cloudflare dashboard > **ds5untethered.com** > **Security > WAF > Rate limiting rules**.
2. Edit the `translate-auth` rule, or create it: **Hostname** equals `translate-auth.ds5untethered.com` (on the Free
   plan: **URI Path** equals `/submit`), **same characteristics:** IP, **rate:** 20 requests per 10 seconds,
   **action:** Block for 10 seconds. **Deploy**.

### No logs

`wrangler.toml` turns Workers observability off, and the code writes no logs. Leave "Logs" and "Traces" off for this
Worker in the dashboard, and do not start `wrangler tail`.

## 5. Point the site at the OAuth App

The translation page reads `site/web/assets/translate/config.js`:

```js
window.TrConfig = {
  oauthClientId: 'Ov23li...',                            // the OAuth App's Client ID (step 1), not the GitHub App's
  workerUrl: 'https://translate-auth.ds5untethered.com', // no trailing slash
  repo: 'Midzer00/ds5-untethered'
};
```

Publish the site as usual (bump `config.js?v=`). All three values are public.

## 6. Check the Worker with curl

This checks that the Worker is deployed and that the OAuth App's Client ID and secret are right, without signing
anyone in. In this folder:

```
curl.exe -s -X POST https://translate-auth.ds5untethered.com/submit -H "Origin: https://ds5untethered.com" -H "Content-Type: text/plain" --data-binary "@smoke-submit.json"
```

The right answer is:

```
{"error":"bad_verification_code"}
```

`smoke-submit.json` is a well-formed request with a made-up sign-in code, so GitHub refuses the code and nothing is
opened. Other answers:

| Answer | Meaning |
| --- | --- |
| `{"error":"exchange_failed"}` | GitHub refused the OAuth App's credentials: the Client ID in `wrangler.toml` or the `OAUTH_CLIENT_SECRET` secret is wrong. Fix it and deploy again. |
| `{"error":"github"}` | A value or secret is missing on the Worker (`OAUTH_CLIENT_ID`, `APP_ID`, `REPO`, `OAUTH_CLIENT_SECRET` or `APP_PRIVATE_KEY`; check `npx wrangler secret list`), or GitHub could not be reached. |
| `{"error":"origin"}` | The `Origin` header is missing or misspelt in the command. |
| `{"error":"bad_redirect"}` | The `redirect_uri` in `smoke-submit.json` was changed; it must be one of the two callback pages. |
| `{"error":"rate_limited"}` | Too many tries from your connection; wait a minute. |

This check does not use the private key. Step 7 does.

## 7. Test with a second GitHub account

Use an account that is not a collaborator on ds5-untethered, in a private window.

1. Open `https://ds5untethered.com/translate/`, pick a language and translate two or three strings.
2. Press **Submit Translation**, then **Send with GitHub**.
3. GitHub's consent screen names "DS5 Untethered Translate" and shows only public data. Press **Authorize**.
4. The page shows a link to the new issue. Open it. It must be opened by `ds5-untethered-translate[bot]` and show
   `**Sender:** <test account> (id ..., verified)` under the hidden marker, without an `@`.
5. Within a few minutes the intake workflow comments with the pull request link and closes the issue.
6. In the pull request, open **Commits**. It must say "<test account> authored and github-actions[bot] committed".
   The PR title is `Translation: <code> by @<test account>`.
7. Send once more with "Do not list my name among the translators" ticked. GitHub shows the consent screen again
   (the Worker deleted the first grant). That commit must be authored by github-actions[bot], carry
   `Translator-Listing: hidden`, and the PR title has no name.
8. Close the test pull requests without merging, and delete their branches.

If the page reports the error `installation`, check the App ID, the private key and the installation (step 2). With
`issue_failed`, check that the App has **Issues: Read and write** and that you accepted it.

A send whose answer got lost (for example, the connection dropped) cannot be repeated with the same sign-in: GitHub
accepts each code only once, so the Worker answers `bad_verification_code` and opens nothing. The first issue is
there; the volunteer just signs in again if they want to send again.

The **DCO** check on these pull requests shows "Action required". That does not block merging; see "The DCO check"
below.

## 8. Rotate every secret afterwards

Once everything works, replace every secret that existed during the setup. Then a copy that ended up somewhere by
mistake (a screenshot, a chat, a file, the clipboard history) is worthless.

1. **OAuth App client secret:** OAuth App page > **Generate a new client secret** > `npx wrangler secret put
   OAUTH_CLIENT_SECRET` with the new one > back on GitHub, **Delete** the old secret. Run the curl check in step 6
   again.
2. **GitHub App private key:** App page > **Private keys > Generate a private key** > pipe the new file into
   `npx wrangler secret put APP_PRIVATE_KEY` as in step 4 > on GitHub, **Delete** the old key (the list shows each
   key's fingerprint and date) > delete the new `.pem` file from Downloads. Send one test translation again (step 7).
3. **Old GitHub App client secrets:** make sure none is left (step 2.5).
4. **Only one App private key:** on the App page, **Private keys** must list exactly one key, the one the Worker uses
   now. Delete any other.
5. **Clipboard history:** if you copied a secret, press **Win+V** and choose **Clear all** (or **Settings > System >
   Clipboard > Clear clipboard data**). Windows keeps copied text there otherwise, and with clipboard sync on, on
   your other devices too.
6. **Cloudflare:** run `npx wrangler logout` when you are done, so this computer no longer holds a Wrangler login.

Do the same once a year, or at once if a secret may have leaked.

### What these keys can do

The intake trusts the `**Sender:**` line in every issue the App's bot opens. So whoever holds the **GitHub App's
private key**, or can **deploy or edit this Worker** in Cloudflare (and so read or replace its secrets), can open
issues naming any GitHub account as the verified sender, and the intake then makes commits authored by that account.
Treat both like the keys to the repository:

- keep two-factor sign-in on your GitHub and Cloudflare accounts, and give nobody else access to the Cloudflare
  account or its API tokens;
- keep exactly one App private key (step 8.4) and never store the `.pem` file;
- if either may have leaked, uninstall the App at once ("Pause or stop"), then rotate.

## Pause or stop

- **Pause sending:** set `oauthClientId: ''` in `config.js` and publish. The page hides Send with GitHub.
- **Stop the bot at once:** uninstall the App from ds5-untethered (**Settings > Applications > Installed GitHub
  Apps**). The Worker then answers `installation`.
- **Remove the Worker:** `npx wrangler delete`.
- **Sign everyone out:** OAuth App page > **Revoke all user tokens**. The Worker already deletes each grant right after
  use, so this is only a safety net.

## The DCO check

The repository has the **DCO** GitHub App (probot/dco) installed. It checks that every commit in a pull request
carries a `Signed-off-by:` line from its author, and skips commits whose author is a bot. Intake pull requests used to
pass because the bot was the author. Now the volunteer is the author and the workflow does not sign off for them, so
the DCO check shows "Action required" on them. Pull requests from the web editor (way 1) show it too.

The check does not block merging today: `main` has no required status checks. Pick one:

1. **Leave it.** Merge as usual, or press **Set DCO to pass** on the check (shown to maintainers).
2. **Turn it off.** If the project does not ask contributors for a DCO sign-off, uninstall the DCO App
   (**Settings > Applications > Installed GitHub Apps > DCO > Configure**), or remove this repository from its list.
3. **Keep it binding.** Then volunteers must sign off themselves. The page would need a checkbox in which the volunteer
   states the DCO, and only then may the intake add their `Signed-off-by:` line. That is a separate decision; the
   workflow never signs off on someone's behalf.

## What GitHub's and Cloudflare's docs say, and what is only expected

Checked on 2026-11-26.

Verified in the docs:

- **OAuth Apps support PKCE.** `GET https://github.com/login/oauth/authorize` takes `code_challenge` and
  `code_challenge_method` (`S256` only), and `POST https://github.com/login/oauth/access_token` takes `code_verifier`,
  "required if `code_challenge` was sent". A request with no `scope` gets an empty scope list. Source:
  [Authorizing OAuth apps](https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/authorizing-oauth-apps).
- **Callback URL:** a `redirect_uri` must have the same host and port, and its path must be "a subdirectory of the
  callback URL". At the token exchange, `redirect_uri` is "strongly recommended", not required. Same page.
- **Revoking:** `DELETE /applications/{client_id}/grant` with Basic auth (client id and secret) and the token in
  the body deletes the authorization and all its tokens, so the user sees the consent screen again next time
  (`.../token` would delete only the token). Source:
  [REST API endpoints for OAuth authorizations](https://docs.github.com/en/rest/apps/oauth-applications).
- **App tokens:** a JWT signed with RS256 by the App's private key, `exp` at most 10 minutes ahead;
  `GET /repos/{owner}/{repo}/installation`, then `POST /app/installations/{id}/access_tokens` with `repositories` and
  `permissions` to narrow the token. Source: [Authenticating as a GitHub App installation](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation).
- **Commit author and committer:** in `POST /repos/{owner}/{repo}/git/commits`, the committer defaults to the author
  when only the author is given. The intake therefore names github-actions[bot] as the committer explicitly. Source:
  [REST API endpoints for Git commits](https://docs.github.com/en/rest/git/commits#create-a-commit).
- **Cloudflare rate limiting binding:** periods of 10 or 60 seconds only, counted per location. So the hourly cap
  needs the KV store. Source: [Rate Limiting](https://developers.cloudflare.com/workers/runtime-apis/bindings/rate-limit/).
- **Cloudflare limits (Free plan):** 50 subrequests and 10 ms CPU per request. A send uses at most about 35
  subrequests (the 1.5 MB request cap allows about 25 comments). Source:
  [Workers limits](https://developers.cloudflare.com/workers/platform/limits/).

Expected, not stated in the docs:

- That a GitHub App's installation token creating an issue triggers the intake workflow. GitHub suppresses only
  events made with a workflow's own token. Step 7 confirms it.
- That a very large send (close to 1.5 MB) fits the Free plan's 10 ms CPU. Normal translation files are far smaller
  and fit in one part. If a huge one fails, the Workers Paid plan raises the limit.
- That GitHub refuses the exchange (`redirect_uri_mismatch`, shown as `exchange_failed`) when the `redirect_uri`
  sent with the code differs from the authorize request's. The Worker's allowlist of the two callback pages holds
  either way. Step 7 confirms that the matching address goes through.
