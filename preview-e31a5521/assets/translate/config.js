/* Send with GitHub settings. All three values are public; the client secret lives only in the Worker.
   While clientId or workerUrl is empty, the page hides Send with GitHub and offers the other two ways.
   See OWNER-SETUP.md for where each value comes from. */
window.TrConfig = {
  // The GitHub App's Client ID (starts with "Iv"), from the App's settings page
  clientId: '',
  // The token Worker, without a trailing slash, e.g. https://translate-auth.ds5untethered.com
  workerUrl: '',
  // owner/name of the repository the issue goes to
  repo: 'Midzer00/ds5-untethered'
};
