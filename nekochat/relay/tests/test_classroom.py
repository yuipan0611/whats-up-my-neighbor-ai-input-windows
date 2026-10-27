"""Classroom admission and directed (never broadcast) multi-student messaging."""
from contextlib import ExitStack
import hashlib
import json
from pathlib import Path
import jsonschema
import sys
import socket
import time
import httpx
import pytest
from fastapi.testclient import TestClient
from websockets.exceptions import InvalidStatus
from websockets.sync.client import connect
from nekochat.relay.app import Account, Policy, create_app
from nekochat.relay.classroom import ClassroomRoom, ip_user
from nekochat.relay.desktop import DesktopBootstrap
from test_desktop import RoomProcess, ROOM

JOIN = 'classroom-test-admission-credential'
HOST = 'classroom-test-host-credential'
HOST_IP = '127.0.0.4'

def digest(value):
    return hashlib.sha256(value.encode()).hexdigest()

def classroom_bootstrap():
    user = ip_user(HOST_IP)
    return {'version': 1, 'room_id': ROOM, 'bind_hosts': [HOST_IP],
        'policy': {'mode': 'lan', 'users': {user: {'token_sha256': digest(HOST), 'friends': []}}},
        'classroom': {'join_sha256': digest(JOIN), 'host_user': user, 'host_ip': HOST_IP}}

def headers(token=JOIN):
    return {'Authorization': f'Bearer {token}', 'X-NekoChat-Room': ROOM}

def state():
    return ClassroomRoom(ROOM, digest(JOIN), ip_user(HOST_IP), HOST_IP, Account(token_sha256=digest(HOST), friends=[]))

def test_classroom_bootstrap_rejects_shared_host_secret_or_invalid_bind():
    value = classroom_bootstrap(); DesktopBootstrap.model_validate(value)
    value['classroom']['join_sha256'] = digest(HOST)
    with pytest.raises(ValueError): DesktopBootstrap.model_validate(value)
    value = classroom_bootstrap(); value['classroom']['host_ip'] = '8.8.8.8'
    with pytest.raises(ValueError): DesktopBootstrap.model_validate(value)
    value = classroom_bootstrap(); value['policy']['mode'] = 'vps'
    with pytest.raises(ValueError): DesktopBootstrap.model_validate(value)

def test_admission_uses_tcp_ip_rejects_spoofing_duplicates_expiry_and_capacity():
    room = state()
    policy = Policy(mode='lan', users={room.host_user: room.members[room.host_user].account})
    app = create_app(policy, ROOM, room)
    with TestClient(app, client=('192.168.1.20', 12345)) as c:
        path = '/nekochat/v1/classroom/join'
        assert c.post(path).status_code == 401
        assert c.post(path, headers=headers('wrong')).status_code == 401
        assert c.post(path, headers={**headers(), 'X-NekoChat-Room': 'wrong'}).status_code == 401
        assert c.post(path, headers={**headers(), 'Origin': 'https://example.com'}).status_code == 403
        assert c.post(path+'?ip=192.168.1.21', headers=headers()).status_code == 403
        assert c.post(path, headers=headers(), json={'ip': '192.168.1.21'}).status_code == 400
        response = c.post(path, headers={**headers(), 'X-Forwarded-For': '192.168.1.99'})
        session = response.json()
        schema=json.loads((Path(__file__).resolve().parents[2]/"protocol/classroom-admission-v1.schema.json").read_text())
        jsonschema.validate(session,schema,format_checker=jsonschema.FormatChecker())
        assert response.headers['cache-control'] == 'no-store'
        assert session['ip'] == '192.168.1.20' and session['user'] == 'ip-192-168-1-20'
        assert session['teacher_ip'] == HOST_IP and session['token'] not in (JOIN, HOST)
        assert room.account(session['user'], '192.168.1.21') is None
        assert c.post(path, headers=headers()).status_code == 409
        room.members[session['user']].expires = time.monotonic()-1
        retry = c.post(path, headers=headers()).json()
        assert retry['token'] != session['token']
        # Registered-but-never-connected sessions cannot occupy room slots forever.
        room.members[retry['user']].expires = time.monotonic()-1
        room.MAX_MEMBERS = 1
        assert c.post(path, headers=headers()).status_code == 503
        for _ in range(10):
            limited = c.post(path, headers=headers())
        assert limited.status_code == 429
    with TestClient(app, client=('8.8.8.8', 12345)) as c:
        assert c.post(path, headers=headers()).status_code == 403

def test_static_relay_has_no_classroom_admission():
    room = state()
    policy = Policy(mode='lan', users={room.host_user: room.members[room.host_user].account})
    with TestClient(create_app(policy)) as c:
        assert c.post('/nekochat/v1/classroom/join', headers=headers()).status_code == 404


def test_many_students_ping_teacher_and_private_reply_without_broadcast():
    try:
        with socket.socket() as probe: probe.bind((HOST_IP, 0))
    except OSError:
        if sys.platform != 'darwin': raise
        pytest.skip('Multiple 127/8 loopback addresses unavailable; real multi-IP transport runs on Windows/Linux CI')
    room = RoomProcess(classroom_bootstrap())
    try:
        url = f'ws://127.0.0.1:{room.port}/nekochat/v1/ws'
        def enroll(ip):
            with httpx.Client(transport=httpx.HTTPTransport(local_address=ip), trust_env=False) as c:
                r = c.post(f'http://127.0.0.1:{room.port}/nekochat/v1/classroom/join', headers=headers())
                assert r.status_code == 200
                return r.json()
        def ws_for(session, source):
            return connect(url, proxy=None, source_address=(source, 0), additional_headers={
                'Authorization': f"Bearer {session['user']}:{session['token']}"})
        a = enroll('127.0.0.2'); b = enroll('127.0.0.3')
        assert a['user'] != b['user'] and a['token'] != b['token']
        # Another source cannot use a student's issued credential or the room join credential.
        with pytest.raises(InvalidStatus):
            with ws_for(a, '127.0.0.3'): pass
        with pytest.raises(InvalidStatus):
            with ws_for({'user': a['user'], 'token': JOIN}, '127.0.0.2'): pass
        with pytest.raises(InvalidStatus):
            with ws_for({'user': ip_user(HOST_IP), 'token': JOIN}, '127.0.0.2'): pass
        with ExitStack() as stack:
            teacher = stack.enter_context(connect(url, proxy=None, additional_headers={'Authorization': f'Bearer {ip_user(HOST_IP)}:{HOST}'}))
            student_a = stack.enter_context(ws_for(a, '127.0.0.2'))
            student_b = stack.enter_context(ws_for(b, '127.0.0.3'))
            for peer in (teacher, student_a, student_b): assert json.loads(peer.recv(timeout=5))['kind'] == 'ready'
            with pytest.raises(InvalidStatus):
                with ws_for(a, '127.0.0.2'): pass
            def send(peer, to, ident, text):
                peer.send(json.dumps({'protocol': 'nekochat', 'version': 1, 'kind': 'chat.send', 'id': ident, 'to': to, 'text': text}))
            # Both students may speak immediately; the teacher receives each source IP.
            send(student_a, ip_user(HOST_IP), 'a_teacher', '老師我有問題 A')
            send(student_b, ip_user(HOST_IP), 'b_teacher', '老師我有問題 B')
            incoming = [json.loads(teacher.recv(timeout=5)) for _ in range(2)]
            assert {e['sender'] for e in incoming} == {a['user'], b['user']}
            assert json.loads(student_a.recv(timeout=5))['status'] == 'delivered'
            assert json.loads(student_b.recv(timeout=5))['status'] == 'delivered'
            # Teacher replies to A only. B never receives A's message or this reply.
            send(teacher, a['user'], 'reply_a', '請 A 說明')
            assert json.loads(student_a.recv(timeout=5))['text'] == '請 A 說明'
            assert json.loads(teacher.recv(timeout=5))['status'] == 'delivered'
            with pytest.raises(TimeoutError): student_b.recv(timeout=.2)
            send(student_a, b['user'], 'a_b', '同學私訊')
            assert json.loads(student_b.recv(timeout=5))['text'] == '同學私訊'
            assert json.loads(student_a.recv(timeout=5))['status'] == 'delivered'
            with pytest.raises(TimeoutError): teacher.recv(timeout=.2)
            send(student_a, 'classroom', 'broadcast_no', 'no broadcast')
            assert json.loads(student_a.recv(timeout=5))['code'] == 'forbidden'
            send(student_a, a['user'], 'self_no', 'no self')
            assert json.loads(student_a.recv(timeout=5))['code'] == 'forbidden'
            send(student_a, ip_user('127.0.0.99'), 'absent_no', 'not a member')
            assert json.loads(student_a.recv(timeout=5))['code'] == 'forbidden'
            student_a.close()
        # Leaving revokes the individual token; joining again issues a fresh one.
        deadline = time.monotonic()+3
        while True:
            try:
                newer = enroll('127.0.0.2'); break
            except AssertionError:
                assert time.monotonic()<deadline
                time.sleep(.05)
        assert newer['token'] != a['token']
        with pytest.raises(InvalidStatus):
            with ws_for(a, '127.0.0.2'): pass
        room.process.stdin.close()
        assert room.process.wait(timeout=8) == 0
        with pytest.raises(httpx.TransportError): httpx.get(f'http://127.0.0.1:{room.port}/healthz', timeout=1)
    finally: room.close()


def test_teacher_and_two_students_directed_delivery_in_process():
    room = state()
    policy = Policy(mode='lan', users={room.host_user: room.members[room.host_user].account})
    app = create_app(policy, ROOM, room)
    # Simulate distinct TCP source addresses in the test harness only. Production
    # has no forwarded-header or client-selected-IP middleware.
    async def simulated_lan(scope, receive, send):
        scope = dict(scope)
        header = dict(scope.get('headers', [])).get(b'x-test-lan-source', b'127.0.0.1').decode()
        scope['client'] = (header, 12345)
        await app(scope, receive, send)
    with TestClient(simulated_lan) as c:
        sessions = []
        for ip in ('192.168.1.21', '192.168.1.22'):
            r = c.post('/nekochat/v1/classroom/join', headers={**headers(), 'X-Test-LAN-Source': ip})
            assert r.status_code == 200
            sessions.append(r.json())
        a, b = sessions
        def open_peer(session):
            return c.websocket_connect('/nekochat/v1/ws', headers={'Authorization': f"Bearer {session['user']}:{session['token']}", 'X-Test-LAN-Source': session['ip']})
        def send(peer, to, ident):
            peer.send_json({'protocol':'nekochat','version':1,'kind':'chat.send','id':ident,'to':to,'text':ident})
        def no_leaks(peer):
            peer.send_json({'protocol':'nekochat','version':1,'kind':'ping','id':'probe'})
            assert peer.receive_json()['kind'] == 'pong'
        with c.websocket_connect('/nekochat/v1/ws', headers={'Authorization': f'Bearer {room.host_user}:{HOST}'}) as teacher, open_peer(a) as sa, open_peer(b) as sb:
            for peer in (teacher, sa, sb): assert peer.receive_json()['kind'] == 'ready'
            send(sa, room.host_user, 'a_teacher'); send(sb, room.host_user, 'b_teacher')
            incoming = [teacher.receive_json(), teacher.receive_json()]
            assert {e['sender'] for e in incoming} == {a['user'], b['user']}
            assert sa.receive_json()['kind'] == 'chat.ack'
            assert sb.receive_json()['kind'] == 'chat.ack'
            no_leaks(sa); no_leaks(sb)
            send(teacher, a['user'], 'reply_a')
            assert sa.receive_json()['text'] == 'reply_a'
            assert teacher.receive_json()['kind'] == 'chat.ack'
            no_leaks(sb)
            send(sa, b['user'], 'private_b')
            assert sb.receive_json()['text'] == 'private_b'
            assert sa.receive_json()['kind'] == 'chat.ack'
            no_leaks(teacher)
