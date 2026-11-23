import contextlib
import http.client
import http.server
import json
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import uuid
from pathlib import Path

xray, sing = (str(Path(value).resolve()) for value in sys.argv[1:])
body = b'pomogator-reality-reference' * 1024
bind = '127.0.0.1'

def free_ports(count):
    sockets = [socket.socket() for _ in range(count)]
    try:
        for sock in sockets:
            sock.bind((bind, 0))
        return [sock.getsockname()[1] for sock in sockets]
    finally:
        for sock in sockets:
            sock.close()

def start(stack, *args, diagnostics):
    proc = subprocess.Popen(args, stdout=diagnostics, stderr=diagnostics)
    def stop():
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(3)
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
    stack.callback(stop)
    return proc

def ready(proc, port):
    for _ in range(50):
        if proc.poll() is not None:
            return False
        try:
            with socket.create_connection((bind, port), timeout=.1):
                return True
        except OSError:
            time.sleep(.1)
    return False

def config(directory, name, values):
    dest = directory / (name + '.json')
    dest.write_text(json.dumps(values))
    return dest

class Destination(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def log_message(self, *args):
        pass

with tempfile.TemporaryDirectory(prefix='pomogator-reality-') as temporary, contextlib.ExitStack() as stack:
    path = Path(temporary)
    quiet = stack.enter_context((path / 'trace').open('wb'))
    cover_port, peer_port, xray_port, sing_port = free_ports(4)
    private_key, public_key = subprocess.check_output([xray, 'x25519'], text=True).splitlines()[:2]
    private_key = private_key.split(': ', 1)[1]
    public_key = public_key.split(': ', 1)[1]
    user = str(uuid.uuid4())
    short_id = '015ca039'
    cover = 'fixture.example'
    flow = 'xtls-rprx-vision'

    dest = http.server.HTTPServer((bind, 0), Destination)
    stack.callback(dest.server_close)
    destination_port = dest.server_address[1]
    for name, subject, san in [('cover', cover, 'DNS:' + cover), ('web', bind, 'IP:' + bind)]:
        subprocess.run(['openssl', 'req', '-x509', '-nodes', '-newkey', 'rsa:2048', '-days', '1',
            '-subj', '/CN=' + subject, '-addext', 'subjectAltName=' + san,
            '-keyout', str(path / (name + '.key')), '-out', str(path / (name + '.crt'))],
            check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(path / 'web.crt', path / 'web.key')
    dest.socket = context.wrap_socket(dest.socket, server_side=True)
    trust = ssl.create_default_context(cafile=str(path / 'web.crt'))
    thread = threading.Thread(target=dest.serve_forever, daemon=True)
    thread.start()
    stack.callback(dest.shutdown)

    cover_executable = path / 'reality-cover.exe'
    subprocess.run(['go', 'build', '-o', str(cover_executable), str(Path(__file__).with_name('reality-cover.go'))], check=True)
    cover_proc = start(stack, str(cover_executable), f'{bind}:{cover_port}', str(path / 'cover.crt'),
        str(path / 'cover.key'), diagnostics=quiet)
    server = config(path, 'peer', {'log': {'loglevel':'error'}, 'inbounds':[
        {'listen':bind,'port':peer_port,'protocol':'vless','settings':{'decryption':'none','clients':[{'id':user,'flow':flow}]},
        'streamSettings':{'network':'raw','security':'reality','realitySettings':{
          'target': f'{bind}:{cover_port}', 'serverNames':[cover], 'privateKey':private_key, 'shortIds':[short_id]}}}],
        'outbounds':[{'protocol':'freedom','settings': {'finalRules':[
            {'action':'allow','ip':[bind+'/32'],'port':str(destination_port)}]}}]})
    assert ready(cover_proc, cover_port), 'TLS cover did not start'
    peer = start(stack, xray, 'run', '-c', str(server), diagnostics=quiet)
    assert ready(peer, peer_port), 'Synthetic server did not start'
    for fingerprint in ("chrome", "firefox"):
        xray_config = config(path, 'xray', {'log': {'loglevel': 'none'},
            'inbounds': [{'listen': bind, 'port': xray_port, 'protocol': 'http'}],
            'outbounds': [{'protocol': 'vless', 'settings': {'vnext': [{'address': bind, 'port': peer_port,
                'users': [{'id': user, 'encryption': 'none', 'flow': flow}]}]},
                'streamSettings': {'network': 'raw', 'security': 'reality',
                    'realitySettings': {'fingerprint': fingerprint, 'serverName': cover,
                        'password': public_key, 'shortId': short_id}}}]})
        sing_config = config(path, 'sing', {'log': {'level': 'error'},
            'inbounds': [{'type': 'http', 'listen': bind, 'listen_port': sing_port}],
            'outbounds': [{'type': 'vless', 'server': bind, 'server_port': peer_port, 'uuid': user, 'flow': flow,
                'tls': {'enabled': True, 'server_name': cover, 'utls': {'enabled': True, 'fingerprint': fingerprint},
                    'reality': {'enabled': True, 'public_key': public_key, 'short_id': short_id}}}]})
        for name, exe, local_port, settings in (('Xray', xray, xray_port, xray_config),
            ('sing-box', sing, sing_port, sing_config)):
            with contextlib.ExitStack() as client_scope:
                client = start(client_scope, exe, 'run', '-c', str(settings), diagnostics=quiet)
                assert ready(client, local_port), f'{name} did not start'
                with contextlib.closing(http.client.HTTPSConnection(bind, local_port, timeout=8, context=trust)) as client_http:
                    client_http.set_tunnel(bind, destination_port)
                    client_http.request('GET', '/fixture')
                    with client_http.getresponse() as response:
                        assert response.status == 200 and response.read() == body, f'{name} ({fingerprint}): unexpected HTTPS response'
                print(f'PASS {name} ({fingerprint}): verified HTTPS body via the same synthetic TCP VLESS/REALITY Vision profile.')
