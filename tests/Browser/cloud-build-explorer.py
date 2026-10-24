"""LOCAL client regression checks with intercepted provider responses. Requires Playwright and Edge.

The cloud markup is read from the real Razor partial; .NET endpoint tests cover Razor rendering/auth.
No SOURCE, provider account, token or real build API is contacted.
"""
from pathlib import Path
from urllib.parse import urlparse, parse_qs
import json
from playwright.sync_api import sync_playwright, expect

ROOT = Path(__file__).resolve().parents[2]
BASE = "http://127.0.0.1:51998"
PROJECT = "11111111-1111-1111-1111-111111111111"
REPO = "22222222-2222-2222-2222-222222222222"
requests = []
mode = {"error": None, "empty": False, "hold": False}
held = []

def html(provider):
    form = '<form method="get" action="/TfsGitBuilds"><label>Source Provider<select id="source-provider" name="provider">'
    for value in ["Tfs2018", "GitHub", "AzureDevOps"]:
        form += f'<option value="{value}" {"selected" if provider == value else ""}>{value}</option>'
    form += '</select></label><button type="submit">Load provider</button></form>'
    if provider == "Tfs2018":
        return form + '<label>Application<select><option>Portal</option></select></label><p>TFS recent builds</p>'
    partial = (ROOT / "src/DeploymentPortal/Views/TfsGitBuilds/_CloudExplorer.cshtml").read_text(encoding="utf-8")
    partial = partial.split('\n', 1)[1].replace('@Model.Provider', provider)
    partial = partial.replace('@Url.Action("Browse", "TfsGitBuilds")', '/TfsGitBuilds/Browse')
    for expression, result in [
        ('@(Model.Provider == "GitHub" ? "GitHub workflow runs" : "Azure DevOps builds")', 'GitHub workflow runs' if provider == 'GitHub' else 'Azure DevOps builds'),
        ('@(Model.Provider == "GitHub" ? "GitHub Source" : "Azure DevOps Source")', 'GitHub Source' if provider == 'GitHub' else 'Azure DevOps Source')]:
        partial = partial.replace(expression.replace('@Model.Provider', provider), result)
    marker = '@if (Model.Provider == "AzureDevOps") {'
    start = partial.index(marker)
    end = partial.index('    }', start)
    content = partial[start + len(marker):end] if provider == "AzureDevOps" else ''
    partial = partial[:start] + content + partial[end + len('    }'):]
    return form + partial + '<script src="/js/cloud-build-explorer.js"></script>'

def build_page(number=42):
    return {"items": [], "artifacts": [], "next": "next-builds" if number == 42 else None,
            "builds": [{"id": number, "number": f"CI-{number}", "branch": "feature/main", "commit": "abc123",
                        "status": "completed", "result": "success", "hasArtifacts": number == 42}]}

def fulfill(route, body, status=200):
    route.fulfill(status=status, content_type="application/json", body=json.dumps(body))

def api(route):
    assert route.request.method == "GET"
    params = {key: values[0] for key, values in parse_qs(urlparse(route.request.url).query).items()}
    assert not any("credential" in key.lower() or "token" in key.lower() for key in params)
    requests.append(params)
    operation = params["operation"]
    if mode["error"]:
        fulfill(route, {"error": "Provider rate limit reached. Wait before refreshing."}, mode["error"])
        return
    if operation == "builds" and mode["hold"]:
        held.append(route)
        return
    response = {"items": [], "builds": [], "artifacts": [], "next": None}
    if not mode["empty"]:
        if operation == "sources": response["items"] = [{"id": "1", "name": "Registered source"}]
        if operation == "projects":
            response["items"] = [{"id": PROJECT, "name": "Project"}]
            response["next"] = None if params.get("cursor") else "next-project"
        if operation == "repositories":
            response["items"] = [{"id": REPO if params["provider"] == "AzureDevOps" else "app", "name": "<img src=x onerror=alert(1)>", "private": True}]
            response["next"] = None if params.get("cursor") else "next-repository"
        if operation == "branches":
            response["items"] = [{"id": "feature/main", "name": "feature/main"}]
            response["next"] = None if params.get("cursor") else "next-branch"
        if operation == "builds": response = build_page(43 if params.get("cursor") else 42)
        if operation == "artifacts":
            assert params["buildId"] == "42"
            response["artifacts"] = [{"name": "<script>secret</script>", "type": "Workflow artifact", "size": 123}]
            response["next"] = None if params.get("cursor") else "next-artifact"
    fulfill(route, response)

with sync_playwright() as p:
    browser = p.chromium.launch(channel="msedge", headless=True)
    page = browser.new_page()
    page.route(BASE + "/TfsGitBuilds/Browse?*", api)
    page.route(BASE + "/TfsGitBuilds*", lambda route: route.fulfill(content_type="text/html", body=html(parse_qs(urlparse(route.request.url).query).get("provider", ["Tfs2018"])[0])))
    # Last registered route wins: register the more specific metadata route after the page route.
    page.unroute(BASE + "/TfsGitBuilds/Browse?*", api)
    page.route(BASE + "/TfsGitBuilds/Browse?*", api)
    page.route(BASE + "/js/cloud-build-explorer.js", lambda route: route.fulfill(content_type="application/javascript", body=(ROOT / "src/DeploymentPortal/wwwroot/js/cloud-build-explorer.js").read_text(encoding="utf-8")))
    page.goto(BASE + "/TfsGitBuilds")
    expect(page.get_by_text("TFS recent builds")).to_be_visible()
    assert not requests
    mode["empty"] = True
    page.locator("#source-provider").select_option("GitHub")
    page.get_by_role("button", name="Load provider", exact=True).click()
    expect(page.locator("#cloud-status")).to_contain_text("No GitHub source configured")
    expect(page.locator("#cloud-source")).to_be_disabled()
    expect(page.locator("#cloud-repository")).to_be_disabled()
    expect(page.locator("#cloud-branch")).to_be_disabled()
    expect(page.locator("#cloud-error")).to_be_hidden()
    assert len(requests) == 1
    assert set(requests[0]) == {"provider", "operation"}
    mode["empty"] = False
    page.locator("#cloud-retry").click()
    expect(page.locator("#cloud-source")).to_be_enabled()
    expect(page.locator("#cloud-source")).to_have_value("")
    assert all(request["operation"] == "sources" for request in requests)
    for provider in ["GitHub", "AzureDevOps"]:
        page.locator("#source-provider").select_option(provider)
        page.get_by_role("button", name="Load provider", exact=True).click()
        expect(page.locator("#cloud-source")).to_be_enabled()
        page.locator("#cloud-source").select_option("1")
        if provider == "AzureDevOps":
            expect(page.locator("#cloud-project")).to_be_enabled()
            page.locator("#more-projects").click()
            expect(page.locator("#more-projects")).to_be_hidden()
            page.locator("#cloud-project").select_option(PROJECT)
        expect(page.locator("#cloud-repository")).to_be_enabled()
        page.locator("#more-repositories").click()
        expect(page.locator("#more-repositories")).to_be_hidden()
        assert page.locator("#cloud-repository img").count() == 0
        page.locator("#cloud-repository").select_option(REPO if provider == "AzureDevOps" else "app")
        expect(page.locator("#cloud-branch")).to_be_enabled()
        page.locator("#more-branches").click()
        expect(page.locator("#more-branches")).to_be_hidden()
        page.locator("#cloud-branch").select_option("feature/main")
        mode["hold"] = True
        page.locator("#cloud-load").click()
        expect(page.locator("#cloud-status")).to_contain_text("Loading recent builds")
        expect(page.locator("#cloud-load")).to_be_disabled()
        assert held
        fulfill(held.pop(), build_page())
        mode["hold"] = False
        expect(page.locator("#cloud-builds")).to_contain_text("CI-42")
        page.get_by_role("link", name="View Artifacts").click()
        expect(page.locator("#cloud-artifacts")).to_be_visible()
        expect(page.locator("#cloud-artifact-list")).to_contain_text("<script>secret</script>")
        assert page.locator("#cloud-artifact-list script").count() == 0
        page.locator("#more-artifacts").click()
        expect(page.locator("#more-artifacts")).to_be_hidden()
        page.locator("#more-builds").click()
        expect(page.locator("#cloud-builds")).to_contain_text("CI-43")
        assert page.get_by_role("link", name="View Artifacts").count() == 0
        page.locator("#cloud-refresh").click()
        expect(page.locator("#cloud-builds")).to_contain_text("CI-42")
        mode["error"] = 429
        before = len(requests)
        page.locator("#cloud-refresh").click()
        expect(page.locator("#cloud-error")).to_contain_text("rate limit")
        expect(page.locator("#cloud-refresh")).to_be_enabled()
        assert len(requests) == before + 1
        assert page.locator("#cloud-builds tr").count() == 0
        mode["error"] = None
    mode["empty"] = True
    page.locator("#cloud-retry").click()
    expect(page.locator("#cloud-status")).to_contain_text("No Azure DevOps source configured")
    expect(page.locator("#cloud-source")).to_be_disabled()
    expect(page.locator("#cloud-load")).to_be_disabled()
    page.locator("#source-provider").select_option("Tfs2018")
    page.get_by_role("button", name="Load provider", exact=True).click()
    expect(page.get_by_text("TFS recent builds")).to_be_visible()
    assert page.locator("#cloud-build-explorer").count() == 0
    assert all(request["provider"] in ["GitHub", "AzureDevOps"] for request in requests)
    browser.close()
print("PASS: initial source requests without selectors, explicit missing GitHub/Azure source messages, no premature selection errors, provider switching, dynamic repositories/branches, pagination, refresh, artifacts, safe rendering and errors.")
