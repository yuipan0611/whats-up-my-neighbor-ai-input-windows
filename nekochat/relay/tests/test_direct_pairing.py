"""LAN IP pairing: explicit host consent, source-IP binding, rate limits, revocation."""
import hashlib
import json
from fastapi.testclient import TestClient
from nekochat.relay.app import Account, Policy, create_app
from nekochat.relay.classroom import ClassroomRoom, ip_user
from nekochat.relay.desktop import DesktopBootstrap

ROOM = 'e' * 32
HOST_IP = '192.168.24.116'
HOST_TOKEN = 'host-only-test-secret'
JOIN_TOKEN = 'unrelated-classroom-invitation'

def digest(value):
    return hashlib.sha256(value.encode()).hexdigest()

def build(direct=True):
    user = ip_user(HOST_IP)
    room = ClassroomRoom(ROOM, digest(JOIN_TOKEN), user, HOST_IP,
                         Account(token_sha256=digest(HOST_TOKEN), friends=[]), direct)
    app = create_app(Policy(mode='lan', users={user: room.members[user].account}), ROOM, room)

    # Test-only socket peer substitution: production never reads this header.
    async def lan(scope, receive, send):
        scope = dict(scope)
        if scope['type'] in ('http', 'websocket'):
            source = dict(scope.get('headers', [])).get(b'x-test-source', b'127.0.0.1').decode()
            scope['client'] = (source, 51313)
        await app(scope, receive, send)
    return TestClient(lan), room

def headers(ip):
    return {'X-Test-Source': ip}

def host_headers(ip='127.0.0.1', secret=HOST_TOKEN):
    return {'X-Test-Source': ip, 'Authorization': 'Bearer ' + secret, 'X-NekoChat-Room': ROOM}

def create(client, ip):
    response = client.post('/nekochat/v1/classroom/request', headers=headers(ip))
    assert response.status_code == 200
    data = response.json()
    assert data['state'] == 'pending' and data['room_id'] == ROOM
    assert len(data['request_id']) == 32 and 'token' not in data
    return data['request_id']

def test_desktop_requires_explicit_fixed_port_to_enable_ip_pairing():
    cfg = {'version': 1, 'room_id': ROOM, 'bind_hosts': [HOST_IP], 'bind_port': 48361,
           'policy': {'mode': 'lan', 'users': {ip_user(HOST_IP):
               {'token_sha256': digest(HOST_TOKEN), 'friends': []}}},
           'classroom': {'join_sha256': digest(JOIN_TOKEN),
                         'host_user': ip_user(HOST_IP), 'host_ip': HOST_IP, 'direct_enabled': True}}
    DesktopBootstrap.model_validate(cfg)
    for bad_port in (0, 80, 48362):
        wrong = dict(cfg, bind_port=bad_port)
        try:
            DesktopBootstrap.model_validate(wrong)
        except ValueError:
            pass
        else:
            raise AssertionError('Direct pairing must bind the fixed port')
    legacy = dict(cfg, bind_port=0, classroom=dict(cfg['classroom'], direct_enabled=False))
    DesktopBootstrap.model_validate(legacy)

def test_unapproved_request_cannot_join_and_host_controls_are_local_only():
    with build()[0] as c:
        ip = '192.168.24.117'
        key = create(c, ip)
        assert c.post('/nekochat/v1/classroom/join', headers=headers(ip)).status_code == 401
        assert c.get('/nekochat/v1/classroom/request/' + key, headers=headers(ip)).status_code == 202
        assert c.get('/nekochat/v1/classroom/request/' + key, headers=headers('192.168.24.118')).status_code == 403
        assert c.get('/nekochat/v1/classroom/pending', headers=headers(ip)).status_code == 403
        assert c.get('/nekochat/v1/classroom/pending', headers=host_headers(secret='wrong')).status_code == 401
        assert c.get('/nekochat/v1/classroom/pending', headers=host_headers(ip)).status_code == 403
        pending = c.get('/nekochat/v1/classroom/pending', headers=host_headers()).json()['pending']
        assert pending == [{'request_id': key, 'ip': ip}]
        assert c.post('/nekochat/v1/classroom/decision/'+key+'/approve', headers=host_headers(ip)).status_code == 403
        assert c.post('/nekochat/v1/classroom/decision/'+key+'/approve', headers=host_headers(secret='wrong')).status_code == 401
        assert c.post('/nekochat/v1/classroom/decision/'+key+'/approve', headers=host_headers()).json()['state'] == 'approved'
        assert not c.get('/nekochat/v1/classroom/pending', headers=host_headers()).json()['pending']
        assert c.get('/nekochat/v1/classroom/request/'+key, headers=headers('192.168.24.118')).status_code == 403
        admitted = c.get('/nekochat/v1/classroom/request/'+key, headers=headers(ip))
        assert admitted.status_code == 200
        session = admitted.json()
        assert session['ip'] == ip and session['user'] == ip_user(ip)
        assert session['teacher_ip'] == HOST_IP and len(session['token']) == 43
        assert session['token'] != JOIN_TOKEN and session['token'] != HOST_TOKEN
        assert c.get('/nekochat/v1/classroom/request/'+key, headers=headers(ip)).status_code == 410
        with c.websocket_connect('/nekochat/v1/ws',
                headers={'Authorization': f"Bearer {session['user']}:{session['token']}", **headers(ip)}) as ws:
            assert ws.receive_json()['kind'] == 'ready'

def test_deny_does_not_issue_token_and_request_is_one_time():
    with build()[0] as c:
        ip = '192.168.24.119'
        key = create(c, ip)
        assert c.post('/nekochat/v1/classroom/decision/'+key+'/deny', headers=host_headers()).status_code == 200
        assert c.get('/nekochat/v1/classroom/request/'+key, headers=headers(ip)).status_code == 403
        assert c.get('/nekochat/v1/classroom/request/'+key, headers=headers(ip)).status_code == 410
        assert c.post('/nekochat/v1/classroom/decision/'+key+'/approve', headers=host_headers()).status_code == 404
        assert c.post('/nekochat/v1/classroom/request', headers=headers(ip)).status_code == 200

def test_request_sources_and_bounds():
    with build()[0] as c:
        path = '/nekochat/v1/classroom/request'
        assert c.post(path, headers={'X-Test-Source':'8.8.8.8'}).status_code == 403
        assert c.post(path, headers={**headers('192.168.24.121'), 'Origin':'https://other-site.test'}).status_code == 403
        assert c.post(path, json={'command': 'execute'}, headers=headers('192.168.24.121')).status_code == 400
        first = create(c, '192.168.24.121')
        assert c.post(path, headers={**headers('192.168.24.121'),'X-Forwarded-For':'192.168.24.130'}).status_code == 409
        assert c.get('/nekochat/v1/classroom/request/'+first, headers=headers('192.168.24.130')).status_code == 403
        assert c.get('/nekochat/v1/classroom/request/not-a-request', headers=headers('192.168.24.121')).status_code == 410
        for _ in range(8):
            c.post(path, headers=headers('192.168.24.121'))
        assert c.post(path, headers=headers('192.168.24.121')).status_code == 429

def test_legacy_rooms_do_not_enable_direct_pairing():
    with build(False)[0] as c:
        assert c.post('/nekochat/v1/classroom/request', headers=headers('192.168.24.118')).status_code == 404
        health = c.get('/healthz').json()
        assert health['admission_version'] == 1 and 'ip_pairing_version' not in health
