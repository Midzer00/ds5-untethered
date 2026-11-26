/* Send with GitHub settings. All three values are public; the OAuth client secret and the GitHub App's private key
   live only in the Worker. While oauthClientId or workerUrl is empty, the page hides Send with GitHub and offers
   the other two ways. See OWNER-SETUP.md for where each value comes from. */
window.TrConfig = {
  // The Client ID of the OAuth App "DS5 Untethered Translate" (not the GitHub App's), from the OAuth App's settings page.
  // The App requests no scope; its callback URL must cover this site's translate/oauth-callback.html.
  oauthClientId: 'Ov23liHBZezVjeTgG5qL',
  // The Worker that serves POST /submit, without a trailing slash, e.g. https://translate-auth.ds5untethered.com
  // Another address must also be added to connect-src in the CSP of translate/index.html, or the page cannot reach it.
  workerUrl: 'https://translate-auth.ds5untethered.com',
  // owner/name of the repository the issue goes to
  repo: 'Midzer00/ds5-untethered'
};
