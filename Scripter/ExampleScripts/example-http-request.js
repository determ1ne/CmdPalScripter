async function f() {
  const http = new HttpClient();
  http.Timeout = TimeSpan.FromSeconds(15);
  const responseText = await http.GetStringAsync('https://example.com/');
  return `Fetched ${responseText.length} characters from https://example.com/`;
}

f()
