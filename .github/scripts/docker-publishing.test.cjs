const { test } = require('node:test');
const assert = require('node:assert/strict');
const { validateReleaseTag, promoteLatest, recoveryContext } = require('./docker-publishing.cjs');

test('release validation requires a SemVer tag emitted by metadata-action', () =>
{
    for (const version of ['0.0.0', '1.2.3', '1.2.3-0', '1.2.3-rc.1', '1.2.3-alpha--beta', '1.2.3-x-y-z.--', '1.2.3--alpha', '1.2.3-rc-'])
    {
        assert.doesNotThrow(() => validateReleaseTag(`v${version}`, { tags: [`ghcr.io/example/app:${version}`, `ghcr.io/example/app:v${version}`] }));
    }
    for (const tag of ['', '1.2.3', 'v01.2.3', 'v1.2.03', 'v1.2.3-', 'v1.2.3-rc..1', 'v1.2.3-01', 'v1.2.3-rc.01'])
    {
        // Rejected SemVer values produce only the raw prefixed tag and SHA tag.
        assert.throws(() => validateReleaseTag(tag, { tags: [`ghcr.io/example/app:${tag}`, 'ghcr.io/example/app:sha-1234567'] }));
    }
    assert.throws(() => validateReleaseTag('v1.2.3+build', { tags: ['ghcr.io/example/app:1.2.3'] }));
});

function fixture(overrides = {})
{
    const commands = [];
    const requests = [];
    const options = {
        context: { eventName: 'release', payload: { release: { tag_name: 'v1.2.3', prerelease: false } }, repo: { owner: 'example', repo: 'app' } },
        github: { rest: { repos: { getLatestRelease: async repo =>
        {
            requests.push(repo);
            return { data: { tag_name: 'v1.2.3' } };
        } } } },
        core: { info: () => {} },
        exec: { exec: async (command, args) => commands.push({ command, args }) },
        image: 'ghcr.io/example/app:1.2.3', digest: `sha256:${'a'.repeat(64)}`, manual: false,
        ...overrides
    };
    return { options, commands, requests };
}

test('current stable release promotes the exact built digest', async () =>
{
    const f = fixture();
    assert.equal(await promoteLatest(f.options), true);
    assert.deepEqual(f.requests, [{ owner: 'example', repo: 'app' }]);
    assert.deepEqual(f.commands, [{ command: 'docker', args: [
        'buildx', 'imagetools', 'create', '--prefer-index=false',
        '--tag', 'ghcr.io/example/app:latest', `${f.options.image}@${f.options.digest}`
    ] }]);
});

test('superseded retry and delayed older build never update latest', async () =>
{
    const f = fixture();
    f.options.github.rest.repos.getLatestRelease = async () => ({ data: { tag_name: 'v1.2.4' } });
    assert.equal(await promoteLatest(f.options), false);
    f.options.context.payload.release.tag_name = 'v1.2.4';
    assert.equal(await promoteLatest(f.options), true);
    const promotionCount = f.commands.length;
    f.options.context.payload.release.tag_name = 'v1.2.3';
    assert.equal(await promoteLatest(f.options), false);
    assert.equal(f.commands.length, promotionCount);
});

test('prerelease flag and prerelease suffix each exclude promotion', async () =>
{
    for (const release of [{ tag_name: 'v1.2.3', prerelease: true }, { tag_name: 'v1.2.3-alpha--beta', prerelease: false }])
    {
        const f = fixture();
        f.options.context.payload.release = release;
        assert.equal(await promoteLatest(f.options), false);
        assert.equal(f.commands.length, 0);
        assert.equal(f.requests.length, 0);
    }
});

test('manual runs need an explicit override and bypass release lookup', async () =>
{
    const f = fixture();
    f.options.context.eventName = 'workflow_dispatch';
    assert.equal(await promoteLatest(f.options), false);
    f.options.manual = true;
    assert.equal(await promoteLatest(f.options), true);
    assert.equal(f.requests.length, 0);
    f.options.context.eventName = 'push';
    assert.equal(await promoteLatest(f.options), false);
    assert.equal(f.commands.length, 1);
});

test('API errors prevent promotion and registry errors propagate', async () =>
{
    const f = fixture();
    f.options.github.rest.repos.getLatestRelease = async () => { throw new Error('API unavailable'); };
    await assert.rejects(promoteLatest(f.options), /API unavailable/);
    assert.equal(f.commands.length, 0);
    f.options.context.eventName = 'workflow_dispatch';
    f.options.manual = true;
    f.options.exec.exec = async () => { throw new Error('Registry unavailable'); };
    await assert.rejects(promoteLatest(f.options), /Registry unavailable/);
});

test('missing build outputs cannot produce a latest tag', async () =>
{
    const f = fixture({ digest: '' });
    await assert.rejects(promoteLatest(f.options), /successfully built image and digest/);
    assert.equal(f.commands.length, 0);
});

test('recovery verifies the published digest and retains the latest-release guard', async () =>
{
    const f = fixture();
    f.options.context.eventName = 'workflow_dispatch';
    f.options.exec.getExecOutput = async () => ({ stdout: `Name: example\nDigest: ${f.options.digest}\n` });
    const recovery = { context: f.options.context, exec: f.options.exec, tag: 'v1.2.3', image: f.options.image, digest: f.options.digest };
    f.options.context = await recoveryContext(recovery);
    assert.equal(await promoteLatest(f.options), true);
    assert.deepEqual(f.requests, [{ owner: 'example', repo: 'app' }]);
    f.options.github.rest.repos.getLatestRelease = async () => ({ data: { tag_name: 'v1.2.4' } });
    assert.equal(await promoteLatest(f.options), false);
    assert.equal(f.commands.length, 1);
    await assert.rejects(recoveryContext({ ...recovery, digest: `sha256:${'b'.repeat(64)}` }), /match the published/);
    for (const tag of ['v1.2.3-rc.1', 'v01.2.3', 'invalid'])
    {
        await assert.rejects(recoveryContext({ ...recovery, tag }), /stable release/);
    }
});
