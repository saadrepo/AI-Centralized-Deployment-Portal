"""LOCAL browser checks for the provisioning helper; requires Playwright and Edge.

Uses intercepted metadata/command responses and synthetic references only.
Run: python tests/Browser/source-credentials.py
"""
from pathlib import Path
import json
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[2]
HTML = """<select id="Provider"><option>GitHub</option><option>Tfs2018</option></select>
<input id="Destination" value="https://api.github.com/orgs/example/">
<select id="CredentialReference"><option value="">TFS</option><option value="old" selected>Old</option></select>
<div id="credential-provisioning" data-command-url="/Sources/ProvisioningCommand"></div>
<code id="provisioning-command"></code><p id="provisioning-command-status"></p>
<button id="copy-provisioning-command" disabled>Copy PowerShell Command</button>
<button id="refresh-credentials" data-url="/Sources/Credentials">Refresh Credentials</button>
<p id="credential-refresh-status"></p>"""

with sync_playwright() as p:
    browser = p.chromium.launch(channel="msedge", headless=True)
    page = browser.new_page()
    page.route("http://127.0.0.1:51999/", lambda route: route.fulfill(body=HTML, content_type="text/html"))
    command = '& "C:\\AdminTools\\DeploymentPortal\\Manage-SourceCredential.ps1" -Action Provision -Provider GitHub -Destination "https://api.github.com/orgs/example/" -AppPoolName "DeploymentPortal"'
    mode = {"command": 200, "refresh": 200, "items": []}
    command_requests = []

    def instructions(route):
        command_requests.append(route.request.url)
        route.fulfill(status=mode["command"], content_type="application/json", body=json.dumps(
            {"command": command} if mode["command"] == 200 else {"error": "Invalid organization URL"}))

    def metadata(route):
        route.fulfill(status=mode["refresh"], content_type="application/json", body=json.dumps(mode["items"]))

    page.route("**/Sources/ProvisioningCommand?*", instructions)
    page.route("**/Sources/Credentials", metadata)
    page.goto("http://127.0.0.1:51999/")
    page.evaluate("Object.defineProperty(navigator, 'clipboard', {value: {writeText: async text => { window.copied = text; }}})")
    page.add_script_tag(path=str(ROOT / "src/DeploymentPortal/wwwroot/js/source-credentials.js"))

    def open_help():
        page.evaluate("document.getElementById('credential-provisioning').dispatchEvent(new Event('show.bs.modal'))")

    open_help()
    expect(page.locator("#copy-provisioning-command")).to_be_enabled()
    expect(page.locator("#provisioning-command")).to_have_text(command)
    assert "provider=GitHub" in command_requests[-1]
    page.locator("#copy-provisioning-command").click()
    expect(page.locator("#provisioning-command-status")).to_contain_text("Command copied")
    assert page.evaluate("window.copied") == command
    page.locator("#Destination").fill("https://api.github.com/users/example/")
    expect(page.locator("#copy-provisioning-command")).to_be_disabled()
    expect(page.locator("#provisioning-command")).to_be_empty()
    mode["command"] = 400
    open_help()
    expect(page.locator("#provisioning-command-status")).to_have_text("Invalid organization URL")
    expect(page.locator("#copy-provisioning-command")).to_be_disabled()
    mode["command"] = 403
    open_help()
    expect(page.locator("#provisioning-command-status")).to_contain_text("Access denied")
    page.locator("#Provider").select_option("Tfs2018")
    before = len(command_requests)
    open_help()
    expect(page.locator("#provisioning-command-status")).to_contain_text("No PAT provisioning command")
    assert len(command_requests) == before

    mode["items"] = [
        {"reference": "old", "provider": 1, "destination": "https://api.github.com/orgs/example/"},
        {"reference": "new", "provider": 1, "destination": "<img src=x onerror=alert(1)>"}]
    page.locator("#refresh-credentials").click()
    expect(page.locator("#credential-refresh-status")).to_contain_text("Credentials refreshed")
    expect(page.locator("#CredentialReference")).to_have_value("old")
    expect(page.locator("#CredentialReference option")).to_have_count(3)
    assert "GitHub" in page.locator("#CredentialReference option").nth(1).inner_text()
    assert page.locator("#CredentialReference img").count() == 0
    page.locator("#CredentialReference").select_option("new")
    mode["refresh"] = 503
    page.locator("#refresh-credentials").click()
    expect(page.locator("#credential-refresh-status")).to_contain_text("refresh failed")
    expect(page.locator("#CredentialReference")).to_have_value("new")
    expect(page.locator("#refresh-credentials")).to_be_enabled()
    mode["refresh"] = 200
    mode["items"] = []
    page.locator("#refresh-credentials").click()
    expect(page.locator("#credential-refresh-status")).to_contain_text("TFS connections remain available")
    expect(page.locator("#CredentialReference")).to_have_value("")
    browser.close()
print("PASS: command copy, invalidation, invalid destination, authorization failure, TFS help, metadata refresh, selection, safe text, failed refresh and empty store.")
