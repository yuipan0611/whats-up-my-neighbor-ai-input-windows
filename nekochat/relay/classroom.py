"""Ephemeral LAN classroom admission. IP addresses select recipients, never grant admission."""
import hashlib
import hmac
import ipaddress
import secrets
import time
from collections import OrderedDict, deque
from dataclasses import dataclass
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from .app import Account


def private_ip(value: str) -> str | None:
    try:
        ip = ipaddress.IPv4Address(value)
        if str(ip) == value and any(ip in ipaddress.IPv4Network(n) for n in (
            '10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16', '127.0.0.0/8')):
            return value
    except ipaddress.AddressValueError:
        pass
    return None


def ip_user(ip: str) -> str:
    if not private_ip(ip):
        raise ValueError('Expected private IPv4')
    return 'ip-' + ip.replace('.', '-')


@dataclass
class Member:
    account: Account
    ip: str
    expires: float
    active: bool = False


@dataclass
class PairRequest:
    source_ip: str
    created: float
    state: str = 'pending'
    session: dict | None = None


class ClassroomRoom:
    MAX_MEMBERS = 100
    MAX_REQUESTS = 32
    REQUEST_TTL = 60

    def __init__(self, room_id: str, join_sha256: str, host_user: str, host_ip: str, host_account: Account,
                 direct_enabled: bool = False):
        if host_user != ip_user(host_ip) or hmac.compare_digest(join_sha256, host_account.token_sha256):
            raise ValueError('Invalid classroom host/admission policy')
        self.room_id = room_id
        self.join_sha256 = join_sha256
        self.host_user = host_user
        self.host_ip = host_ip
        self.members = {host_user: Member(host_account, host_ip, float('inf'), True)}
        self.attempts = OrderedDict()
        self.direct_enabled = direct_enabled
        self.requests: OrderedDict[str, PairRequest] = OrderedDict()

    def prune(self):
        now = time.monotonic()
        for user, member in list(self.members.items()):
            if not member.active and member.expires <= now:
                del self.members[user]
        for request_id, entry in list(self.requests.items()):
            if entry.created + self.REQUEST_TTL <= now:
                del self.requests[request_id]

    def rate_limit(self, source: str):
        now = time.monotonic()
        rate = self.attempts.setdefault(source, deque())
        self.attempts.move_to_end(source)
        while len(self.attempts) > 256:
            self.attempts.popitem(last=False)
        while rate and rate[0] <= now - 10:
            rate.popleft()
        if len(rate) >= 10:
            raise HTTPException(429, 'Try again later')
        rate.append(now)

    def admit(self, source: str) -> dict:
        self.prune()
        user = ip_user(source)
        if user in self.members:
            raise HTTPException(409, 'This IP is already in the room')
        if len(self.members) >= self.MAX_MEMBERS:
            raise HTTPException(503, 'Classroom is full')
        token = secrets.token_urlsafe(32)
        self.members[user] = Member(Account(token_sha256=hashlib.sha256(token.encode()).hexdigest(), friends=[]),
                                    source, time.monotonic() + 30)
        return {'protocol': 'nekochat', 'version': 1, 'room_id': self.room_id,
                'user': user, 'ip': source, 'teacher_ip': self.host_ip, 'token': token}

    def account(self, user: str, source_ip: str):
        self.prune()
        member = self.members.get(user)
        if member is None or not private_ip(source_ip):
            return None
        if user != self.host_user and member.ip != source_ip:
            return None
        return member.account

    def activate(self, user: str):
        self.members[user].active = True

    def release(self, user: str):
        if user != self.host_user:
            self.members.pop(user, None)

    def authorized(self, sender: str, target: str) -> bool:
        self.prune()
        return sender != target and sender in self.members and target in self.members

    def install(self, app: FastAPI):
        @app.post('/nekochat/v1/classroom/join')
        async def join(request: Request):
            source = request.client.host if request.client else ''
            if request.query_params or request.headers.get('origin') or not private_ip(source):
                raise HTTPException(403, 'Native private LAN clients only')
            # Bound failed admission work; never trust X-Forwarded-For or a supplied IP/user.
            self.rate_limit(source)
            auth = request.headers.get('authorization', '')
            if request.headers.get('x-nekochat-room') != self.room_id or not auth.startswith('Bearer ') or not hmac.compare_digest(
                hashlib.sha256(auth[7:].encode()).hexdigest(), self.join_sha256):
                raise HTTPException(401, 'Room invitation required')
            # This route has no body or client-selected identity, and is versioned independently.
            if request.headers.get('content-length', '0') != '0' or request.headers.get('transfer-encoding'):
                raise HTTPException(400, 'No request body allowed')
            return JSONResponse(self.admit(source), headers={'Cache-Control': 'no-store'})

        # IP pairing never bypasses host approval. All host-control APIs are loopback-only
        # and require the host's *own* secret (not the classroom invitation token).
        def pair_source(request: Request, host_only: bool = False, no_body: bool = False) -> str:
            if not self.direct_enabled:
                raise HTTPException(404, 'IP pairing is disabled')
            source = request.client.host if request.client else ''
            if (request.query_params or request.headers.get('origin') or not private_ip(source)
                    or (host_only and not source.startswith('127.'))):
                raise HTTPException(403, 'Private LAN only')
            if no_body and (request.headers.get('content-length', '0') != '0' or
                            request.headers.get('transfer-encoding')):
                raise HTTPException(400, 'No request body allowed')
            if host_only:
                auth = request.headers.get('authorization', '')
                if (request.headers.get('x-nekochat-room') != self.room_id or
                        not auth.startswith('Bearer ') or
                        not hmac.compare_digest(hashlib.sha256(auth[7:].encode()).hexdigest(),
                                                self.members[self.host_user].account.token_sha256)):
                    raise HTTPException(401, 'Host authorization required')
            return source

        @app.post('/nekochat/v1/classroom/request')
        async def request_pair(request: Request):
            source = pair_source(request, no_body=True)
            self.rate_limit(source)
            self.prune()
            if ip_user(source) in self.members:
                raise HTTPException(409, 'This IP is already connected')
            if any(p.source_ip == source for p in self.requests.values()):
                raise HTTPException(409, 'Pairing request already pending')
            if len(self.requests) >= self.MAX_REQUESTS:
                raise HTTPException(503, 'Host request queue is full')
            request_id = secrets.token_urlsafe(24)
            self.requests[request_id] = PairRequest(source, time.monotonic())
            return JSONResponse({'protocol': 'nekochat', 'version': 1, 'room_id': self.room_id,
                                 'request_id': request_id, 'state': 'pending'}, headers={'Cache-Control': 'no-store'})

        @app.get('/nekochat/v1/classroom/pending')
        async def pending_pairs(request: Request):
            pair_source(request, host_only=True)
            self.prune()
            return JSONResponse({'pending': [{'request_id': key, 'ip': value.source_ip}
                                             for key, value in self.requests.items() if value.state == 'pending']},
                                headers={'Cache-Control': 'no-store'})

        @app.post('/nekochat/v1/classroom/decision/{request_id}/{decision}')
        async def decide_pair(request_id: str, decision: str, request: Request):
            pair_source(request, host_only=True, no_body=True)
            self.prune()
            if decision not in ('approve', 'deny') or len(request_id) != 32:
                raise HTTPException(404, 'Unknown pairing request')
            entry = self.requests.get(request_id)
            if entry is None or entry.state != 'pending':
                raise HTTPException(404, 'Pairing request expired')
            if decision == 'approve':
                entry.session = self.admit(entry.source_ip)
            entry.state = 'approved' if decision == 'approve' else 'denied'
            return JSONResponse({'state': entry.state}, headers={'Cache-Control': 'no-store'})

        @app.get('/nekochat/v1/classroom/request/{request_id}')
        async def pair_status(request_id: str, request: Request):
            source = pair_source(request)
            self.prune()
            entry = self.requests.get(request_id) if len(request_id) == 32 else None
            if entry is None:
                raise HTTPException(410, 'Pairing request expired')
            if entry.source_ip != source:
                raise HTTPException(403, 'Pairing request belongs to another IP')
            if entry.state == 'pending':
                return JSONResponse({'state': 'pending'}, status_code=202, headers={'Cache-Control': 'no-store'})
            self.requests.pop(request_id, None)
            if entry.state == 'denied':
                raise HTTPException(403, 'Host declined pairing')
            if entry.session is None or entry.session['user'] not in self.members:
                raise HTTPException(410, 'Pairing approval expired')
            return JSONResponse(entry.session, headers={'Cache-Control': 'no-store'})
