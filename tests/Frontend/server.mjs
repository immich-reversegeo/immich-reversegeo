import { spawn } from 'node:child_process';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const runDir = process.env.FRONTEND_TEST_RUN_DIR;
if (!runDir)
{
    throw new Error('Start the fixture server through npm run test:frontend.');
}

// Override inherited application credentials and endpoints; never load a real library.
const env = {
    ...process.env,
    DATA_DIR: join(runDir, 'data'),
    CONFIG_DIR: join(runDir, 'config'),
    DB_HOST: '127.0.0.1',
    DB_HOSTNAME: '127.0.0.1',
    DB_PORT: '1',
    DB_USERNAME: 'frontend_fixture',
    DB_PASSWORD: 'frontend_fixture',
    DB_DATABASE_NAME: 'frontend_fixture',
    IMMICH_URL: '',
    IMMICH_WEB_URL: '',
    IMMICH_API_KEY: '',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1'
};
let child;
let stopping = false;
function stop()
{
    stopping = true;
    child?.kill('SIGTERM');
}
process.on('SIGINT', stop);
process.on('SIGTERM', stop);

function run(args, cwd)
{
    return new Promise((resolve, reject) => {
        child = spawn('dotnet', args, { cwd, env, stdio: 'inherit', windowsHide: true });
        child.once('error', reject);
        child.once('exit', (code, signal) => {
            if (code === 0 || stopping)
            {
                resolve();
            }
            else
            {
                reject(new Error(`Frontend process exited: ${code ?? signal}`));
            }
        });
    });
}

await run(['run', '--project', 'tests/FrontendFixtures/FrontendFixtures.csproj', '--configuration', 'Release', '--', runDir], root);
if (!stopping)
{
    const webDir = join(root, 'src', 'ImmichReverseGeo.Web');
    await run([join(webDir, 'bin', 'Release', 'net10.0', 'ImmichReverseGeo.Web.dll'), '--urls', 'http://127.0.0.1:5265', '--environment', 'Development'], webDir);
}
