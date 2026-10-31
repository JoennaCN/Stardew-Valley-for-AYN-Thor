"""One-shot native validation of DRM dumb-buffer -> DMA-BUF -> EGLImage sharing."""

from __future__ import annotations

import ctypes as C
import os
import time

from lease import LeasedPanel

EGL_NONE = 0x3038
EGL_EXTENSIONS = 0x3055
EGL_OPENGL_API = 0x30A2
EGL_RENDERABLE_TYPE = 0x3040
EGL_OPENGL_BIT = 0x0008
EGL_PLATFORM_SURFACELESS_MESA = 0x31DD
EGL_LINUX_DMA_BUF_EXT = 0x3270
EGL_WIDTH = 0x3057
EGL_HEIGHT = 0x3056
EGL_LINUX_DRM_FOURCC_EXT = 0x3271
EGL_DMA_BUF_PLANE0_FD_EXT = 0x3272
EGL_DMA_BUF_PLANE0_OFFSET_EXT = 0x3273
EGL_DMA_BUF_PLANE0_PITCH_EXT = 0x3274
DRM_FORMAT_XRGB8888 = 0x34325258  # XR24
GBM_BO_USE_SCANOUT = 1 << 0
GBM_BO_USE_RENDERING = 1 << 2
GBM_BO_USE_LINEAR = 1 << 4

GL_TEXTURE_2D = 0x0DE1
GL_TEXTURE_MIN_FILTER = 0x2801
GL_TEXTURE_MAG_FILTER = 0x2800
GL_NEAREST = 0x2600
GL_FRAMEBUFFER = 0x8D40
GL_COLOR_ATTACHMENT0 = 0x8CE0
GL_FRAMEBUFFER_COMPLETE = 0x8CD5
GL_COLOR_BUFFER_BIT = 0x00004000


def proc(egl, name: bytes, result, *args):
    address = egl.eglGetProcAddress(name)
    if not address:
        raise RuntimeError(f"missing procedure {name.decode()}")
    return C.CFUNCTYPE(result, *args)(address)


def main() -> int:
    egl = C.CDLL("libEGL.so.1")
    gl = C.CDLL("libGL.so.1")
    gbm = C.CDLL("libgbm.so.1")
    egl.eglGetProcAddress.restype = C.c_void_p
    egl.eglGetProcAddress.argtypes = [C.c_char_p]
    egl.eglInitialize.argtypes = [C.c_void_p, C.POINTER(C.c_int), C.POINTER(C.c_int)]
    egl.eglBindAPI.argtypes = [C.c_uint]
    egl.eglChooseConfig.argtypes = [C.c_void_p, C.POINTER(C.c_int), C.POINTER(C.c_void_p), C.c_int,
                                    C.POINTER(C.c_int)]
    egl.eglCreateContext.restype = C.c_void_p
    egl.eglCreateContext.argtypes = [C.c_void_p, C.c_void_p, C.c_void_p, C.POINTER(C.c_int)]
    egl.eglMakeCurrent.argtypes = [C.c_void_p, C.c_void_p, C.c_void_p, C.c_void_p]
    egl.eglQueryString.restype = C.c_char_p
    egl.eglQueryString.argtypes = [C.c_void_p, C.c_int]
    egl.eglDestroyContext.argtypes = [C.c_void_p, C.c_void_p]
    egl.eglTerminate.argtypes = [C.c_void_p]

    get_display = proc(egl, b"eglGetPlatformDisplayEXT", C.c_void_p,
                       C.c_uint, C.c_void_p, C.POINTER(C.c_int))
    display = get_display(EGL_PLATFORM_SURFACELESS_MESA, None, None)
    major, minor = C.c_int(), C.c_int()
    if not display or not egl.eglInitialize(display, C.byref(major), C.byref(minor)):
        raise RuntimeError("eglInitialize failed")
    extensions = (egl.eglQueryString(display, EGL_EXTENSIONS) or b"").decode()
    required = ("EGL_EXT_image_dma_buf_import", "EGL_KHR_surfaceless_context")
    missing = [item for item in required if item not in extensions]
    if missing:
        raise RuntimeError("missing EGL extensions: " + ",".join(missing))
    if not egl.eglBindAPI(EGL_OPENGL_API):
        raise RuntimeError("eglBindAPI failed")
    config = C.c_void_p()
    count = C.c_int()
    config_attrs = (C.c_int * 3)(EGL_RENDERABLE_TYPE, EGL_OPENGL_BIT, EGL_NONE)
    chose_config = egl.eglChooseConfig(display, config_attrs, C.byref(config), 1, C.byref(count))
    if not chose_config or count.value < 1:
        if "EGL_KHR_no_config_context" not in extensions:
            raise RuntimeError("eglChooseConfig failed and no-config contexts are unavailable")
        config = C.c_void_p()
    context_attrs = (C.c_int * 1)(EGL_NONE)
    context = egl.eglCreateContext(display, config, None, context_attrs)
    if not context or not egl.eglMakeCurrent(display, None, None, context):
        raise RuntimeError("surfaceless OpenGL context failed")

    create_image = proc(egl, b"eglCreateImageKHR", C.c_void_p,
                        C.c_void_p, C.c_void_p, C.c_uint, C.c_void_p, C.POINTER(C.c_int))
    destroy_image = proc(egl, b"eglDestroyImageKHR", C.c_uint, C.c_void_p, C.c_void_p)
    image_target = proc(egl, b"glEGLImageTargetTexture2DOES", None, C.c_uint, C.c_void_p)

    gl.glGenTextures.argtypes = [C.c_int, C.POINTER(C.c_uint)]
    gl.glBindTexture.argtypes = [C.c_uint, C.c_uint]
    gl.glTexParameteri.argtypes = [C.c_uint, C.c_uint, C.c_int]
    gl.glGenFramebuffers.argtypes = [C.c_int, C.POINTER(C.c_uint)]
    gl.glBindFramebuffer.argtypes = [C.c_uint, C.c_uint]
    gl.glFramebufferTexture2D.argtypes = [C.c_uint, C.c_uint, C.c_uint, C.c_uint, C.c_int]
    gl.glCheckFramebufferStatus.restype = C.c_uint
    gl.glCheckFramebufferStatus.argtypes = [C.c_uint]
    gl.glViewport.argtypes = [C.c_int, C.c_int, C.c_int, C.c_int]
    gl.glClearColor.argtypes = [C.c_float, C.c_float, C.c_float, C.c_float]
    gl.glClear.argtypes = [C.c_uint]

    panel = LeasedPanel()
    render_fd = -1
    dma_fd = -1
    gbm_device = None
    gbm_bo = None
    image = None
    try:
        buffer_width, buffer_height = panel.height, panel.width
        render_fd = os.open("/dev/dri/renderD128", os.O_RDWR | os.O_CLOEXEC)
        gbm.gbm_create_device.restype = C.c_void_p
        gbm.gbm_create_device.argtypes = [C.c_int]
        gbm.gbm_bo_create.restype = C.c_void_p
        gbm.gbm_bo_create.argtypes = [C.c_void_p, C.c_uint32, C.c_uint32, C.c_uint32, C.c_uint32]
        gbm.gbm_bo_get_fd.argtypes = [C.c_void_p]
        gbm.gbm_bo_get_stride.argtypes = [C.c_void_p]
        gbm.gbm_bo_get_stride.restype = C.c_uint32
        gbm.gbm_bo_destroy.argtypes = [C.c_void_p]
        gbm.gbm_device_destroy.argtypes = [C.c_void_p]
        gbm_device = gbm.gbm_create_device(render_fd)
        gbm_bo = gbm.gbm_bo_create(gbm_device, buffer_width, buffer_height, DRM_FORMAT_XRGB8888,
                                   GBM_BO_USE_SCANOUT | GBM_BO_USE_RENDERING | GBM_BO_USE_LINEAR)
        if not gbm_bo:
            # Freedreno can reject an explicitly linear render target; its
            # native modifier is still shareable through DMA-BUF.
            gbm_bo = gbm.gbm_bo_create(gbm_device, buffer_width, buffer_height, DRM_FORMAT_XRGB8888,
                                       GBM_BO_USE_SCANOUT | GBM_BO_USE_RENDERING)
        if not gbm_bo:
            raise RuntimeError("gbm_bo_create failed")
        dma_fd = gbm.gbm_bo_get_fd(gbm_bo)
        pitch = gbm.gbm_bo_get_stride(gbm_bo)
        if dma_fd < 0:
            raise RuntimeError("gbm_bo_get_fd failed")

        panel._drm.drmPrimeFDToHandle.argtypes = [C.c_int, C.c_int, C.POINTER(C.c_uint32)]
        panel._drm.drmModeAddFB2.argtypes = [C.c_int, C.c_uint32, C.c_uint32, C.c_uint32,
                                             C.POINTER(C.c_uint32), C.POINTER(C.c_uint32),
                                             C.POINTER(C.c_uint32), C.POINTER(C.c_uint32), C.c_uint32]
        handle = C.c_uint32()
        if panel._drm.drmPrimeFDToHandle(panel.fd, dma_fd, C.byref(handle)) != 0:
            raise RuntimeError("drmPrimeFDToHandle failed")
        handles = (C.c_uint32 * 4)(handle.value, 0, 0, 0)
        pitches = (C.c_uint32 * 4)(pitch, 0, 0, 0)
        offsets = (C.c_uint32 * 4)(0, 0, 0, 0)
        fb = C.c_uint32()
        if panel._drm.drmModeAddFB2(panel.fd, buffer_width, buffer_height, DRM_FORMAT_XRGB8888,
                                    handles, pitches, offsets, C.byref(fb), 0) != 0:
            raise RuntimeError("drmModeAddFB2 failed")
        attrs = (C.c_int * 13)(
            EGL_WIDTH, buffer_width,
            EGL_HEIGHT, buffer_height,
            EGL_LINUX_DRM_FOURCC_EXT, DRM_FORMAT_XRGB8888,
            EGL_DMA_BUF_PLANE0_FD_EXT, dma_fd,
            EGL_DMA_BUF_PLANE0_OFFSET_EXT, 0,
            EGL_DMA_BUF_PLANE0_PITCH_EXT, pitch,
            EGL_NONE,
        )
        image = create_image(display, None, EGL_LINUX_DMA_BUF_EXT, None, attrs)
        if not image:
            raise RuntimeError("eglCreateImageKHR dma-buf import failed")
        texture = C.c_uint()
        framebuffer = C.c_uint()
        gl.glGenTextures(1, C.byref(texture))
        gl.glBindTexture(GL_TEXTURE_2D, texture)
        gl.glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST)
        gl.glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST)
        image_target(GL_TEXTURE_2D, image)
        gl.glGenFramebuffers(1, C.byref(framebuffer))
        gl.glBindFramebuffer(GL_FRAMEBUFFER, framebuffer)
        gl.glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, texture, 0)
        status = gl.glCheckFramebufferStatus(GL_FRAMEBUFFER)
        if status != GL_FRAMEBUFFER_COMPLETE:
            raise RuntimeError(f"incomplete framebuffer: 0x{status:x}")
        gl.glViewport(0, 0, buffer_width, buffer_height)
        gl.glClearColor(C.c_float(1.0), C.c_float(0.0), C.c_float(0.0), C.c_float(1.0))
        gl.glClear(GL_COLOR_BUFFER_BIT)
        gl.glFinish()
        first = (C.c_ubyte * 4)()
        gl.glReadPixels.argtypes = [C.c_int, C.c_int, C.c_int, C.c_int, C.c_uint, C.c_uint, C.c_void_p]
        gl.glReadPixels(0, 0, 1, 1, 0x1908, 0x1401, C.byref(first))  # RGBA / UNSIGNED_BYTE
        gl.glFinish()
        first_bytes = bytes(first)
        panel._commit(fb.value, modeset=False, source_width=buffer_width,
                      source_height=buffer_height, rotation=2)
        time.sleep(1)
        print(f"dma-buf-ok egl={major.value}.{minor.value} size={buffer_width}x{buffer_height} "
              f"pitch={pitch} pixel={first_bytes.hex()} fd={dma_fd} fb={fb.value}")
        if first_bytes != b"\xff\x00\x00\xff":
            raise RuntimeError(f"unexpected first pixel {first_bytes.hex()}")
    finally:
        if image:
            destroy_image(display, image)
        if dma_fd >= 0:
            os.close(dma_fd)
        if gbm_bo:
            gbm.gbm_bo_destroy(gbm_bo)
        if gbm_device:
            gbm.gbm_device_destroy(gbm_device)
        if render_fd >= 0:
            os.close(render_fd)
        panel.close()
        egl.eglMakeCurrent(display, None, None, None)
        egl.eglDestroyContext(display, context)
        egl.eglTerminate(display)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
