FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive

RUN apt-get update \
    && apt-get install --no-install-recommends -y openssh-client openssh-server sshpass \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --create-home --shell /bin/bash password-user \
    && useradd --create-home --shell /bin/bash key-user \
    && useradd --create-home --shell /bin/bash interactive-user \
    && echo 'password-user:password-secret' | chpasswd \
    && echo 'interactive-user:interactive-secret' | chpasswd \
    && passwd -d key-user \
    && install -d -m 0755 /run/sshd /srv/remoteflow-fixtures/documents/nested \
    && install -d -m 0755 /opt/remoteflow/host_keys /opt/remoteflow/client_keys \
    && ssh-keygen -q -t ed25519 -N '' -C remoteflow-host-primary -f /opt/remoteflow/host_keys/primary \
    && ssh-keygen -q -t ed25519 -N '' -C remoteflow-host-alternate -f /opt/remoteflow/host_keys/alternate \
    && ssh-keygen -q -t ed25519 -N '' -C remoteflow-key-user -f /opt/remoteflow/client_keys/id_ed25519 \
    && printf 'RemoteFlow SFTP fixture\n' > /srv/remoteflow-fixtures/documents/readme.txt \
    && printf 'Nested fixture\n' > /srv/remoteflow-fixtures/documents/nested/data.txt

COPY sshd_config /etc/ssh/sshd_config
COPY use-host-key /usr/local/bin/use-host-key
COPY presented-host-key-fingerprint /usr/local/bin/presented-host-key-fingerprint

RUN install -d -m 0700 -o key-user -g key-user /home/key-user/.ssh \
    && cp /opt/remoteflow/client_keys/id_ed25519.pub /home/key-user/.ssh/authorized_keys \
    && chown key-user:key-user /home/key-user/.ssh/authorized_keys \
    && chmod 0600 /home/key-user/.ssh/authorized_keys /opt/remoteflow/client_keys/id_ed25519 \
    && chmod 0644 /opt/remoteflow/client_keys/id_ed25519.pub \
    && chmod 0755 /usr/local/bin/use-host-key /usr/local/bin/presented-host-key-fingerprint \
    && /usr/local/bin/use-host-key primary --no-reload

EXPOSE 22

CMD ["/usr/sbin/sshd", "-D", "-e"]
