"""Build the illustrated 0.1.1 user manual from actual application UI renders."""
from pathlib import Path
import base64
import html
import json
import re
from PIL import Image as PILImage
from reportlab.pdfgen import canvas
from reportlab.lib.colors import HexColor, white
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import Paragraph
from reportlab.lib.utils import ImageReader
import pypdfium2 as pdfium
from pypdf import PdfReader

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts' / 'manual'
OUT.mkdir(parents=True, exist_ok=True)
SHOT = OUT / 'images' / 'dashboard.png'
PAIR = OUT / 'images' / 'pairing-entry.png'
PDF = OUT / 'HearthLink-0.1.1-User-Manual.pdf'
HTML = OUT / 'HearthLink-0.1.1-User-Manual.html'
W, H = 612, 792
LEFT, RIGHT, TOP, BOTTOM = 48, 48, 92, 53
CW = W - LEFT - RIGHT
BLUE = HexColor('#2563EB')
NAVY = HexColor('#142541')
MUTED = HexColor('#65748B')
INK = HexColor('#1F2F46')
FONT_DIR = Path('C:/Windows/Fonts')
for name, filename in [('Segoe', 'segoeui.ttf'), ('SegoeBold', 'segoeuib.ttf'), ('Mono', 'consola.ttf')]:
    pdfmetrics.registerFont(TTFont(name, str(FONT_DIR / filename)))
pdfmetrics.registerFontFamily('Segoe', normal='Segoe', bold='SegoeBold', italic='Segoe', boldItalic='SegoeBold')
STYLES = {
    'body': ParagraphStyle('body', fontName='Segoe', fontSize=10.2, leading=14.7, textColor=INK, spaceAfter=8),
    'small': ParagraphStyle('small', fontName='Segoe', fontSize=8.6, leading=11.8, textColor=MUTED),
    'heading': ParagraphStyle('heading', fontName='SegoeBold', fontSize=13, leading=17, textColor=NAVY),
    'code': ParagraphStyle('code', fontName='Mono', fontSize=8.5, leading=12, textColor=INK),
}

def text(value, style='body'):
    return {'kind': 'text', 'value': value, 'style': style}

def step(number, value):
    return {'kind': 'text', 'value': f'<b>{number}.</b> {value}', 'style': 'body'}

def head(value):
    return text(value, 'heading')

def note(value):
    return {'kind': 'note', 'value': value}

def image(caption, crop=None, markers=None, width=None, source='dashboard'):
    return {'kind': 'image', 'caption': caption, 'crop': crop, 'markers': markers or [], 'width': width, 'source': source}

def cols(left, right, fraction=.44):
    return {'kind': 'columns', 'left': left, 'right': right, 'fraction': fraction}

PAGES = [
    ('Start here', [
        text('This manual takes you from installing HearthLink on two Windows 11 PCs to requesting an attended remote desktop session. Follow the numbered steps in order.'),
        note('<b>Current release: 0.1.1.</b> Keep the remote PC signed in and unlocked, with a person available to approve access. A real two-PC screen and control session has not yet been verified.'),
        image('Actual HearthLink dashboard before any computers are paired. Blue numbered circles are instructional annotations.',
              markers=[('1', 540, 210), ('2', 1228, 290), ('3', 1228, 448), ('4', 560, 695)], width=CW),
        text('<b>1 Paired computers:</b> computers you have enrolled. The empty list is normal before pairing.<br/><b>2 This computer:</b> name and host/controller settings.<br/><b>3 Pair with this PC:</b> one-time code and identity fingerprint.<br/><b>4 Who can request access:</b> controllers authorized to ask for a session on this PC.'),
        text('<b>Two names to remember</b><br/><b>PC A - controller:</b> the computer you sit at.<br/><b>PC B - host:</b> the computer whose screen you want to view or control.'),
        text('Screenshots are renders of the actual app controls with an isolated, unenrolled identity. They do not show an established remote connection. Your device name and fingerprint will be different.', 'small'),
    ]),
    ('Install on both computers', [
        text('Perform these steps on both PC A and PC B. The self-contained installer includes the .NET runtime, so a separate .NET installation is not needed.'),
        step(1, 'Copy <b>HearthLink-0.1.1-win-x64-setup.exe</b> to both computers. Use the 0.1.1 installer supplied with this project.'),
        step(2, 'If checking the download, open PowerShell in the folder containing the installer and run the command below. Compare the result with the SHA256SUMS.txt supplied with the release.'),
        text('Get-FileHash .\\HearthLink-0.1.1-win-x64-setup.exe<br/>-Algorithm SHA256', 'code'),
        text('Type that command on one line. The expected 0.1.1 SHA-256 is:<br/>541EF1995A4EA396661EA3BC43DA1A2CF<br/>7018B556320528DF993CCA31FCD439F', 'code'),
        step(3, 'Double-click the installer. It is unsigned, so Windows may show an unknown-publisher or reputation warning. Continue only after you have verified and chosen to trust this particular build. Do not disable Windows security features.'),
        step(4, 'Approve the administrator prompt if you intend to install. Follow the setup wizard and keep the default application folder unless you need another location.'),
        step(5, 'On <b>PC B</b>, select the optional task below. On a computer used only as a controller, leave it unchecked.'),
        note('<b>Allow incoming host connections on Private networks<br/>(TCP 45831, local subnet only)</b>'),
        step(6, 'Finish setup, then launch <b>HearthLink</b> from the Start menu as your normal Windows user.'),
        step(7, 'Keep both computers connected to the same trusted home network, or your approved VPN. Do not configure router port forwarding.'),
        head('If you want both computers to receive connections'),
        text('Select the host firewall task on both PCs. You will enable both roles in the app on the next page, then pair separately in each direction.'),
        text('The default rule permits only HearthLink, TCP 45831, Private network profiles and local-subnet sources. Domain-profile or VPN traffic may require an administrator-scoped rule for the exact approved subnet. Do not open a Public network rule.', 'small'),
    ]),
    ('Set up the controller and host', [
        cols([
            image('This computer panel before incoming sessions are enabled.', crop=[1008, 150, 302, 266], markers=[('1', 1240, 218), ('3', 1030, 260), ('2', 1030, 294)], width=215),
        ], [
            step(1, 'On each PC, edit its name under <b>This computer</b> and click <b>Save</b>. Choose names you can recognize.'),
            step(2, 'On <b>PC A</b>, select <b>Use as a controller</b>. Incoming sessions can remain off if you do not want to control A from another PC.'),
            step(3, 'On <b>PC B</b>, select <b>Allow incoming sessions</b>. Its status should change from <b>Host disabled</b> to <b>Host ready</b>.'),
        ]),
        step(4, 'Leave PC B unlocked and keep HearthLink running. Someone must be able to approve the pairing request and each session at PC B.'),
        head('Find the address of PC B'),
        step(5, 'On PC B, press <b>Windows + R</b>, type <b>cmd</b>, and press Enter. In the command window, type <b>hostname</b> and press Enter. Write down the computer name.'),
        step(6, 'If that name does not work from PC A, type <b>ipconfig</b> on PC B. Write down the <b>IPv4 Address</b> under its connected Wi-Fi or Ethernet adapter. For a VPN connection, use the address assigned to the appropriate VPN adapter.'),
        note('An IP address can change when a router or VPN assigns a new one. A saved hostname is easier to reuse when your network resolves it correctly. Automatic host discovery is not included in 0.1.1.'),
        head('If you want both roles'),
        text('Enable both checkboxes on each PC and install the host firewall rule on each. First complete A-to-B pairing and its test. Repeat in reverse only if B also needs to control A.'),
        text('Names, identities and trust persist for the Windows user who configured them. After a reboot, sign in as that user and launch HearthLink manually. This version does not start a background host automatically.', 'small'),
    ]),
    ('Open pairing and verify the host', [
        text('Have both app windows available. Read PC B\'s fingerprint directly on PC B, or compare it with the person at that computer through a channel you trust.'),
        cols([
            image('PC B before opening a code. The full fingerprint is shown below the pairing button.', crop=[1008, 424, 302, 255], markers=[('1', 1280, 482), ('3', 1280, 604)], width=215),
        ], [
            step(1, 'On <b>PC B</b>, click <b>Open pairing for 2 minutes</b>. A random eight-digit code appears where <b>Pairing is closed</b> was shown.'),
            step(2, 'On <b>PC A</b>, type PC B\'s hostname or IPv4 address into the field under <b>Add a computer on your network</b>. Click <b>Pair a computer</b>.'),
            step(3, 'PC A shows a <b>Verify host identity</b> dialog. Compare every one of its 64 fingerprint digits with <b>This PC\'s identity</b> on PC B.'),
        ]),
        image('The address field and Pair a computer button on PC A. This screenshot is from the initial empty state.', crop=[228, 500, 740, 115], markers=[('2', 942, 515)], width=CW),
        step(4, 'Choose <b>Yes</b> only if the fingerprints match exactly. Choose <b>No</b> if they do not match. Do not turn off identity verification to make the connection work.'),
        note('The pairing code expires after two minutes and can be used once. Five incorrect code attempts close the pairing window. If time runs out, reopen pairing on PC B and begin again.'),
        text('The code is not a permanent password. Do not copy it into session history, screenshots shared with others or a troubleshooting message.', 'small'),
    ]),
    ('Finish pairing on both computers', [
        image('Actual pairing-code entry dialog. The field is blank until you enter the current code from PC B.', source='pairing', width=365),
        step(1, 'On <b>PC A</b>, enter the eight-digit code currently displayed on PC B. Keep any leading zeros. Click <b>Pair computer</b>.'),
        step(2, 'On <b>PC B</b>, a <b>HearthLink pairing</b> dialog asks whether to trust the requesting controller. Check the controller name and compare its full fingerprint with <b>This PC\'s identity</b> on PC A.'),
        step(3, 'If the controller identity is correct, approve the request on PC B. Reject any unexpected or mismatched request.'),
        step(4, 'After success, PC B appears under <b>Paired computers</b> on PC A. PC A appears under <b>Who can request access</b> on PC B. Click <b>Refresh</b> on PC A to update the actual status.'),
        note('<b>Pairing does not open a desktop session.</b> It permits this controller to request one. A separate approval at PC B is still required whenever you connect.'),
        head('If pairing is rejected'),
        text('Check that incoming sessions are enabled on PC B and that the correct code is still current. Open a fresh pairing window, retry from PC A and approve at PC B. If an address is already enrolled, use that saved entry; do not replace its identity casually.'),
    ]),
    ('Connect and use the remote desktop', [
        step(1, 'On <b>PC A</b>, select PC B\'s actual row under <b>Paired computers</b>. Click <b>Refresh</b>. Confirm it is <b>Online</b>. If it is unavailable, use the troubleshooting page.'),
        step(2, 'Choose your mode <b>before</b> connecting. Leave the <b>Keyboard mouse</b> checkbox selected for control. Clear it for view-only access. Then click <b>Connect</b>.'),
        image('Mode and connection controls. Your actual host row will appear above them after successful pairing.', crop=[228, 566, 740, 63], width=CW),
        step(3, 'On <b>PC B</b>, check the incoming-session dialog. It states whether PC A requests viewing or viewing and control. Approve only the request you intended to make.'),
        step(4, 'After approval, PC A\'s viewer should show PC B\'s actual screen. Click inside the remote image to give it keyboard focus. Use only ordinary, non-elevated apps for the first test.'),
        head('Controls in the viewer'),
        text('<b>Display list:</b> choose a monitor to switch the remote image.<br/><b>Full screen:</b> toggle the viewer between full screen and a window. Its toolbar remains available.<br/><b>F12:</b> release keyboard focus to the viewer\'s Disconnect button. Click the remote image to resume input.<br/><b>Disconnect:</b> close the session. Closing the viewer also disconnects.'),
        head('Check that the session is real'),
        text('Open Notepad locally on PC B. From PC A, type a unique, nonsensitive sentence and move the Notepad window. Confirm the sentence and movement directly on PC B. In a separate view-only session, confirm that PC A cannot change PC B\'s desktop.'),
        note('Windows may keep combinations such as Alt+Tab or Windows-key shortcuts local. Ctrl+Alt+Del, sign-in, locked desktops and secure UAC screens are unsupported. Elevated applications can reject input. Return to the normal unlocked desktop and reconnect if necessary.'),
        text('A live viewer screenshot is not included because no two-physical-PC session has yet been verified. Do not treat the dashboard screenshots as proof of a remote connection.', 'small'),
    ]),
    ('Disconnect and remove access', [
        cols([
            image('The host can end a session at any time.', crop=[1008, 306, 302, 112], markers=[('1', 1266, 394)], width=215),
        ], [
            text('<b>From PC A:</b> click <b>Disconnect</b> in the viewer, or close the viewer window.<br/><br/><b>From PC B:</b> click <b>End remote session</b>. Confirm that PC A stops receiving the screen and input no longer affects PC B.'),
        ]),
        head('Stop hosting temporarily'),
        text('On PC B, clear <b>Allow incoming sessions</b>, or close HearthLink completely. The host listener stops. Closing only PC A\'s viewer does not disable future requests to PC B.'),
        head('Revoke a controller permanently'),
        image('Controller permissions on the host before enrollment. After pairing, select the actual controller entry here.', crop=[226, 664, 740, 177], markers=[('2', 315, 783)], width=CW),
        step(1, 'On <b>PC B</b>, select PC A under <b>Who can request access</b>. Check its name and identity before removing it.'),
        step(2, 'Click <b>Revoke controller</b>. The current host session ends and this controller can no longer request access until explicitly paired again.'),
        step(3, 'Optionally, on PC A select PC B under <b>Paired computers</b> and click <b>Forget</b>. Forget removes PC A\'s saved host identity; it does not revoke PC A at PC B. Revoke on the host to remove authorization.'),
        head('Reconnect after a network interruption'),
        text('Close the disconnected viewer. Restore network access, leave PC B unlocked with HearthLink running, click Refresh on PC A, select PC B and Connect again. PC B must approve the new session. Automatic reconnection and reboot recovery are not included.'),
    ]),
    ('Troubleshoot a connection', [
        head('Unavailable or unable to connect'),
        text('Check these items in order: PC B is awake, signed in and unlocked; HearthLink is running; Allow incoming sessions is selected; the hostname or IP address is correct; both PCs can reach each other on the LAN or approved VPN; and the host firewall task was selected during setup.'),
        text('For a network check, open PowerShell on PC A and run the command below. Replace HOSTNAME with PC B\'s actual hostname or IP address. A successful port test does not prove application authentication.', 'body'),
        text('Test-NetConnection HOSTNAME -Port 45831', 'code'),
        head('Identity mismatch or host identity could not be verified'),
        text('Stop and compare PC B\'s full identity in person. Reinstalling or deleting its identity data can change it. Only after confirming a legitimate change should you Forget the old entry and pair again. Never disable TLS checks.'),
        head('Trust revoked or pairing rejected'),
        text('Re-pair only with explicit approval at PC B. For a rejected code, reopen pairing there, enter its current eight digits on PC A and approve the request on PC B. Codes are not reusable and do not last longer than two minutes.'),
        head('Host unavailable with an address or port error'),
        text('Close duplicate HearthLink host instances. Only one user-session host can listen on TCP 45831 on the PC at a time. If another application uses that port, resolve the conflict before retrying.'),
        head('Blank screen or keyboard and mouse do not work'),
        text('Return PC B to its normal unlocked desktop. Avoid secure UAC, protected video and elevated apps. On PC A, confirm the Keyboard mouse checkbox was selected before connecting, then click the remote image. If you need to change mode, disconnect and reconnect with new approval.'),
        head('VPN or domain network is blocked'),
        text('The default firewall rule is Private-profile and local-subnet only. Ask your administrator to scope a rule to the exact HearthLink executable, TCP 45831, the intended profile and your approved VPN or domain subnet. Do not enable Public access or router forwarding.'),
        head('What to include when reporting a problem'),
        text('Record both Windows editions, app version 0.1.1, the failing step and the exact error. Do not include pairing codes, private keys, passwords, clipboard contents or sensitive screen content.'),
    ]),
    ('Daily use and cleanup', [
        head('Before each session'),
        text('Start HearthLink on both PCs. Keep PC B signed in and unlocked with incoming sessions enabled. On PC A, select the actual saved host, refresh, choose view-only or control and connect. Approve at PC B. When finished, disconnect and confirm the host is ready again.'),
        head('What survives a restart'),
        text('The device identity, saved names and enrollment remain for the same Windows user. After reboot, sign in and launch HearthLink yourself. There is no automatic startup, pre-login host or unattended mode in this release.'),
        head('Review session history'),
        text('Click <b>Session history</b> in the main window. If history exists, it opens as metadata records in Notepad; if not, the app says there is no history yet. Records use certificate identities and timestamps. This is not a complete graphical audit viewer.'),
        head('Features not included in 0.1.1'),
        text('Automatic discovery, unattended access, host service or startup after reboot, clipboard sharing, file transfer, remote restart, automatic reconnection, audio and internet relays are not included. There are no working controls for those features. Sign-in, lock-screen and secure UAC control are unsupported.'),
        head('Uninstall'),
        step(1, 'On a host, revoke controllers whose access should be removed, then end any active session and close HearthLink.'),
        step(2, 'Open Windows <b>Settings &gt; Apps &gt; Installed apps</b>, find <b>HearthLink</b>, choose <b>Uninstall</b> and follow the prompts. You can also use its Start-menu uninstaller.'),
        step(3, 'The uninstaller removes the program and the <b>HearthLink Host TCP</b> firewall rule. No service or startup entry was installed by 0.1.1.'),
        step(4, 'Enrollment and history are retained per Windows user. If you also want to remove those, press Windows + R, enter the folder below, and delete only that HearthLink folder after the app is closed. Repeat separately for each user who used the app.'),
        text('%LOCALAPPDATA%\\HearthLink', 'code'),
        note('Deleting this folder removes the saved identity and trust. A later installation will need new pairing. Keep the folder if you want to preserve the same enrollment.'),
    ]),
    ('Record your first two PC test', [
        text('Use this page while testing. The manual describes how to operate the build; it does not certify that your two PCs have passed these checks.'),
        head('Computer details'),
        text('PC A name and Windows edition: __________________________________<br/><br/>PC B name and Windows edition: __________________________________<br/><br/>Date and app version: ____________________________________________'),
        head('Mark each item only after observing it'),
        text('[  ] Installed the same 0.1.1 build on both PCs.<br/><br/>[  ] Compared both full identities and paired with explicit host approval.<br/><br/>[  ] Saw the real host appear Online after an authenticated refresh.<br/><br/>[  ] Declined a session at the host and confirmed no screen was shared.<br/><br/>[  ] Approved view-only and confirmed controller input had no effect.<br/><br/>[  ] Approved control and saw a fresh visual change from the real host.<br/><br/>[  ] Typed a unique sentence in the host\'s Notepad from the controller.<br/><br/>[  ] Disconnected from the controller and confirmed input stopped.<br/><br/>[  ] Ended a second session from the host and confirmed it stopped.<br/><br/>[  ] Revoked the controller and confirmed a new session was rejected.'),
        head('If something fails'),
        text('Stop at the failing step. Record what you expected, what happened and the exact error. Continue using the normal desktop locally until the issue is resolved. Do not mark a loopback test or an illustrated screen as a successful two-PC session.'),
        text('Notes: ________________________________________________________<br/><br/>______________________________________________________________<br/><br/>______________________________________________________________'),
    ]),
]

def paragraph(c, value, x, y, width, style='body'):
    p = Paragraph(value, STYLES[style])
    _, height = p.wrap(width, 1000)
    p.drawOn(c, x, y-height)
    return y-height-8

def draw_image(c, item, x, y, width):
    path = PAIR if item['source']=='pairing' else SHOT
    iw, ih = PILImage.open(path).size
    cx, cy, sw, sh = item['crop'] or [0,0,iw,ih]
    dw = min(width, item['width'] or width)
    dh = dw*sh/sw
    dx = x+(width-dw)/2
    bottom = y-dh
    c.saveState()
    clip=c.beginPath(); clip.rect(dx,bottom,dw,dh)
    c.clipPath(clip,stroke=0,fill=0)
    scale=dw/sw
    c.drawImage(ImageReader(str(path)),dx-cx*scale,bottom-(ih-cy-sh)*scale,width=iw*scale,height=ih*scale,mask='auto')
    c.restoreState()
    c.setStrokeColor(HexColor('#DCE4EF')); c.setLineWidth(.6); c.rect(dx,bottom,dw,dh,stroke=1,fill=0)
    for label, mx, my in item['markers']:
        px=dx+(mx-cx)*scale; py=y-(my-cy)*scale
        c.setFillColor(BLUE); c.circle(px,py,8.5,stroke=0,fill=1)
        c.setFillColor(white); c.setFont('SegoeBold',9); c.drawCentredString(px,py-3,label)
    return paragraph(c,item['caption'],x,bottom-7,width,'small')-4

def draw_items(c, items, x, y, width):
    for item in items:
        if item['kind']=='text':
            if item['style']=='heading': y-=3
            y=paragraph(c,item['value'],x,y,width,item['style'])
        elif item['kind']=='note':
            p=Paragraph(item['value'],STYLES['body']); _,h=p.wrap(width-24,1000)
            c.setFillColor(HexColor('#EFF5FF')); c.roundRect(x,y-h-22,width,h+22,5,stroke=0,fill=1)
            p.drawOn(c,x+12,y-h-10); y-=h+33
        elif item['kind']=='image':
            y=draw_image(c,item,x,y,width)
        elif item['kind']=='columns':
            lw=(width-20)*item['fraction']; rw=width-20-lw
            ly=draw_items(c,item['left'],x,y,lw)
            ry=draw_items(c,item['right'],x+lw+20,y,rw)
            y=min(ly,ry)-4
    return y

c=canvas.Canvas(str(PDF),pagesize=(W,H))
c.setTitle('HearthLink 0.1.1 user manual')
c.setAuthor('HearthLink')
extents=[]
for number,(title,items) in enumerate(PAGES,1):
    c.setFillColor(NAVY); c.setFont('SegoeBold',10); c.drawString(LEFT,H-31,'HearthLink user manual')
    c.setFillColor(MUTED); c.setFont('Segoe',8.5); c.drawRightString(W-RIGHT,H-31,'Version 0.1.1 | 22 October 2026')
    c.setFillColor(HexColor('#000000')); c.setFont('SegoeBold',22); c.drawString(LEFT,H-68,title)
    end=draw_items(c,items,LEFT,H-TOP,CW)
    extents.append({'page':number,'title':title,'content_bottom':round(end,1)})
    if end<BOTTOM: raise RuntimeError(f'Page {number} overflows: bottom={end:.1f}')
    c.setFillColor(MUTED); c.setFont('Segoe',8); c.drawString(LEFT,28,'Attended LAN and VPN access | Windows 11')
    c.drawRightString(W-RIGHT,28,f'{number} / {len(PAGES)}')
    c.showPage()
c.save()

def html_image(item):
    path=PAIR if item['source']=='pairing' else SHOT
    iw,ih=PILImage.open(path).size
    cx,cy,sw,sh=item['crop'] or [0,0,iw,ih]
    encoded=base64.b64encode(path.read_bytes()).decode('ascii')
    image_style=f'width:{iw/sw*100:.5f}%;left:{-cx/sw*100:.5f}%;top:{-cy/sh*100:.5f}%;'
    marks=''.join(f'<span class="marker" style="left:{(mx-cx)/sw*100:.3f}%;top:{(my-cy)/sh*100:.3f}%">{html.escape(label)}</span>' for label,mx,my in item['markers'])
    html_width=(item['width'] or 800)*1.45
    return f'<figure><div class="shot" style="aspect-ratio:{sw}/{sh};max-width:min(100%,{html_width}px)"><img src="data:image/png;base64,{encoded}" style="{image_style}" alt="{html.escape(item["caption"])}">{marks}</div><figcaption>{item["caption"]}</figcaption></figure>'

def html_items(items):
    parts=[]
    for item in items:
        kind=item['kind']
        if kind=='text':
            tag='h3' if item['style']=='heading' else 'p'
            parts.append(f'<{tag} class="{item["style"]}">{item["value"]}</{tag}>')
        elif kind=='note': parts.append(f'<aside>{item["value"]}</aside>')
        elif kind=='image': parts.append(html_image(item))
        elif kind=='columns': parts.append(f'<div class="columns"><div>{html_items(item["left"])}</div><div>{html_items(item["right"])}</div></div>')
    return ''.join(parts)

nav=''.join(f'<a href="#step-{i}">{i}. {html.escape(title)}</a>' for i,(title,_) in enumerate(PAGES,1))
sections=''.join(f'<section id="step-{i}"><h2>{i}. {html.escape(title)}</h2>{html_items(items)}</section>' for i,(title,items) in enumerate(PAGES,1))
HTML.write_text('''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>HearthLink 0.1.1 user manual</title><style>
*{box-sizing:border-box}html{scroll-behavior:smooth}body{margin:0;background:#f2f6fb;color:#1f2f46;font:16px/1.65 "Segoe UI",Arial,sans-serif}header{background:#142541;color:white;padding:36px max(24px,calc((100vw - 980px)/2))}h1{font-size:34px;margin:0}header p{color:#c5d3e8;margin:8px 0}main{max-width:1040px;padding:28px;margin:auto}nav{background:white;padding:20px 24px;border:1px solid #e1e8f1;columns:2}nav a{display:block;color:#2563eb;text-decoration:none;margin:5px 0}a:focus-visible{outline:3px solid #2563eb;outline-offset:3px}section{margin:24px 0;padding:30px;background:white;border:1px solid #e1e8f1;scroll-margin-top:16px}h2{font-size:27px;color:#142541;margin:0 0 20px}h3{font-size:20px;margin:22px 0 10px}p{margin:10px 0}.small,figcaption{font-size:13px;color:#65748b}.code{font-family:Consolas,monospace;background:#f2f6fb;padding:12px;overflow-wrap:anywhere}aside{background:#eff5ff;padding:16px 20px;margin:18px 0;border-left:3px solid #2563eb}figure{margin:20px 0}figcaption{margin-top:8px}.shot{position:relative;overflow:hidden;border:1px solid #dce4ef;margin:auto;max-width:100%}.shot img{position:absolute;height:auto;max-width:none}.marker{position:absolute;transform:translate(-50%,-50%);display:grid;place-items:center;background:#2563eb;color:white;border:1px solid white;width:25px;height:25px;border-radius:50%;font-weight:700;font-size:13px}.columns{display:grid;grid-template-columns:44% 1fr;gap:24px}.columns figure{margin-top:0}.columns p:first-child{margin-top:0}footer{padding:20px 0;color:#65748b;font-size:13px}@media(max-width:700px){main{padding:14px}section{padding:20px}.columns{grid-template-columns:1fr}nav{columns:1}h1{font-size:28px}.marker{width:20px;height:20px;font-size:11px}}@media print{body{background:white}nav{display:none}main{max-width:none;padding:0}section{break-before:page;border:0;padding:0}section:first-of-type{break-before:auto}header{padding:0;color:black;background:white}.shot{break-inside:avoid}aside,figure{break-inside:avoid}}
</style></head><body><header><h1>HearthLink user manual</h1><p>Version 0.1.1 | Step by step with annotated app screenshots</p></header><main><nav aria-label="Contents">'''+nav+'</nav>'+sections+'<footer>Prepared 22 October 2026. This file contains its screenshots and works without an internet connection.</footer></main></body></html>',encoding='utf-8')

reader=PdfReader(str(PDF))
assert len(reader.pages)==len(PAGES)
extracted='\n'.join(page.extract_text() or '' for page in reader.pages)
for required in ['Pair a computer','Revoke controller','End remote session','Keyboard','0.1.1','541EF1995A4EA396661EA3BC43DA1A2CF']:
    assert required in extracted, f'Missing required text: {required}'
assert len(extracted)>15000
manifest=(ROOT/'artifacts'/'SHA256SUMS.txt').read_text(encoding='ascii')
expected_hash=next(line.split()[0] for line in manifest.splitlines() if 'HearthLink-0.1.1-win-x64-setup.exe' in line)
assert expected_hash in re.sub(r'\s+','',extracted), 'Installer checksum in manual does not match release'
(OUT/'manual-layout-check.json').write_text(json.dumps(extents,indent=2),encoding='utf-8')
renders=OUT/'qa'; renders.mkdir(exist_ok=True)
doc=pdfium.PdfDocument(str(PDF))
for index in range(len(doc)):
    page=doc[index]; bitmap=page.render(scale=1.5)
    bitmap.to_pil().save(renders/f'page-{index+1:02}.png')
    bitmap.close(); page.close()
doc.close()
print(f'Created {PDF.name}: {len(PAGES)} pages; verified required content and page boundaries.')
print(f'Created {HTML.name}: self-contained illustrated manual.')
print(json.dumps(extents))
