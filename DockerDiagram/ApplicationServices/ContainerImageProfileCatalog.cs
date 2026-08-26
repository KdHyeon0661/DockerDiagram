using System;
using System.Collections.Generic;
using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public static class ContainerImageProfileCatalog
    {
        private static readonly IReadOnlyList<ContainerImageProfile> Profiles =
        [
            new()
            {
                Id = "nginx",
                DisplayName = "Nginx",
                Category = "Web Server",
                Description = "Static web server or reverse proxy.",
                Notes = ["Mount custom config to /etc/nginx/conf.d or site files to /usr/share/nginx/html."],
                ImageAliases = ["nginx"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "8080", "80"),
                    Port("HTTPS_PORT", "HTTPS host port", "8443", "443", required: false)
                ],
                Volumes =
                [
                    Volume("html", "/usr/share/nginx/html"),
                    Volume("conf", "/etc/nginx/conf.d")
                ]
            },
            new()
            {
                Id = "httpd",
                DisplayName = "Apache HTTP Server",
                Category = "Web Server",
                Description = "Apache httpd web server.",
                Notes = ["Mount web content to /usr/local/apache2/htdocs."],
                ImageAliases = ["httpd"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "8080", "80")
                ],
                Volumes =
                [
                    Volume("htdocs", "/usr/local/apache2/htdocs"),
                    Volume("conf", "/usr/local/apache2/conf")
                ]
            },
            new()
            {
                Id = "caddy",
                DisplayName = "Caddy",
                Category = "Web Server",
                Description = "Caddy web server with simple config and automatic HTTPS support.",
                Notes = ["For local development, 80/443 mapping is enough. Public HTTPS still needs DNS and reachable ports."],
                ImageAliases = ["caddy"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "8080", "80"),
                    Port("HTTPS_PORT", "HTTPS host port", "8443", "443", required: false)
                ],
                Volumes =
                [
                    Volume("site", "/usr/share/caddy"),
                    Volume("data", "/data"),
                    Volume("config", "/config")
                ]
            },
            new()
            {
                Id = "mysql",
                DisplayName = "MySQL",
                Category = "Database",
                Description = "MySQL database with initial database and user.",
                Notes = ["The password variables are only used on first database initialization."],
                ImageAliases = ["mysql"],
                Fields =
                [
                    Text("DB_NAME", "Database name", "app", "MYSQL_DATABASE"),
                    Text("DB_USER", "Database user", "app", "MYSQL_USER"),
                    Password("DB_PASSWORD", "Database password", "MYSQL_PASSWORD"),
                    Password("ROOT_PASSWORD", "Root password", "MYSQL_ROOT_PASSWORD"),
                    Port("HOST_PORT", "Host port", "3306", "3306")
                ],
                Volumes = [Volume("mysql-data", "/var/lib/mysql")]
            },
            new()
            {
                Id = "mariadb",
                DisplayName = "MariaDB",
                Category = "Database",
                Description = "MariaDB database with initial database and user.",
                Notes = ["The password variables are only used on first database initialization."],
                ImageAliases = ["mariadb"],
                Fields =
                [
                    Text("DB_NAME", "Database name", "app", "MARIADB_DATABASE"),
                    Text("DB_USER", "Database user", "app", "MARIADB_USER"),
                    Password("DB_PASSWORD", "Database password", "MARIADB_PASSWORD"),
                    Password("ROOT_PASSWORD", "Root password", "MARIADB_ROOT_PASSWORD"),
                    Port("HOST_PORT", "Host port", "3306", "3306")
                ],
                Volumes = [Volume("mariadb-data", "/var/lib/mysql")]
            },
            new()
            {
                Id = "postgres",
                DisplayName = "PostgreSQL",
                Category = "Database",
                Description = "PostgreSQL database with initial database and user.",
                Notes = ["POSTGRES_PASSWORD is required by the official image."],
                ImageAliases = ["postgres"],
                Fields =
                [
                    Text("DB_NAME", "Database name", "app", "POSTGRES_DB"),
                    Text("DB_USER", "Database user", "postgres", "POSTGRES_USER"),
                    Password("DB_PASSWORD", "Database password", "POSTGRES_PASSWORD"),
                    Port("HOST_PORT", "Host port", "5432", "5432")
                ],
                Volumes = [Volume("postgres-data", "/var/lib/postgresql/data")]
            },
            new()
            {
                Id = "mongodb",
                DisplayName = "MongoDB",
                Category = "Database",
                Description = "MongoDB database with root credentials and optional initial database.",
                Notes = ["Root credentials are created only when the database directory is empty."],
                ImageAliases = ["mongo"],
                Fields =
                [
                    Text("DB_NAME", "Initial database", "app", "MONGO_INITDB_DATABASE"),
                    Text("ROOT_USER", "Root user", "admin", "MONGO_INITDB_ROOT_USERNAME"),
                    Password("ROOT_PASSWORD", "Root password", "MONGO_INITDB_ROOT_PASSWORD"),
                    Port("HOST_PORT", "Host port", "27017", "27017")
                ],
                Volumes = [Volume("mongo-data", "/data/db")]
            },
            new()
            {
                Id = "redis",
                DisplayName = "Redis",
                Category = "Cache",
                Description = "Redis cache with password and append-only persistence.",
                Notes = ["Command is applied only when the Command tab is empty."],
                ImageAliases = ["redis"],
                Fields =
                [
                    Password("PASSWORD", "Redis password"),
                    Port("HOST_PORT", "Host port", "6379", "6379")
                ],
                CommandTemplate = "redis-server --appendonly yes --requirepass \"${PASSWORD}\"",
                Volumes = [Volume("redis-data", "/data")]
            },
            new()
            {
                Id = "valkey",
                DisplayName = "Valkey",
                Category = "Cache",
                Description = "Valkey cache with Redis-compatible options.",
                Notes = ["Valkey is Redis-compatible for most local development scenarios."],
                ImageAliases = ["valkey/valkey", "valkey"],
                Fields =
                [
                    Password("PASSWORD", "Valkey password"),
                    Port("HOST_PORT", "Host port", "6379", "6379")
                ],
                CommandTemplate = "valkey-server --appendonly yes --requirepass \"${PASSWORD}\"",
                Volumes = [Volume("valkey-data", "/data")]
            },
            new()
            {
                Id = "rabbitmq",
                DisplayName = "RabbitMQ",
                Category = "Message Broker",
                Description = "RabbitMQ broker with default user and management UI port.",
                Notes = ["Use an image tag with management, such as rabbitmq:management, for the web UI."],
                ImageAliases = ["rabbitmq"],
                Fields =
                [
                    Text("ADMIN_USER", "Admin user", "admin", "RABBITMQ_DEFAULT_USER"),
                    Password("ADMIN_PASSWORD", "Admin password", "RABBITMQ_DEFAULT_PASS"),
                    Port("AMQP_PORT", "AMQP host port", "5672", "5672"),
                    Port("MANAGEMENT_PORT", "Management host port", "15672", "15672")
                ],
                Volumes = [Volume("rabbitmq-data", "/var/lib/rabbitmq")]
            },
            new()
            {
                Id = "mosquitto",
                DisplayName = "Eclipse Mosquitto",
                Category = "Message Broker",
                Description = "MQTT broker for lightweight pub/sub messaging.",
                Notes = ["The official image expects configuration files under /mosquitto/config for custom auth or listeners."],
                ImageAliases = ["eclipse-mosquitto"],
                Fields =
                [
                    Port("MQTT_PORT", "MQTT host port", "1883", "1883"),
                    Port("WEBSOCKET_PORT", "WebSocket host port", "9001", "9001", required: false)
                ],
                Volumes =
                [
                    Volume("config", "/mosquitto/config"),
                    Volume("data", "/mosquitto/data"),
                    Volume("log", "/mosquitto/log")
                ]
            },
            new()
            {
                Id = "minio",
                DisplayName = "MinIO",
                Category = "Object Storage",
                Description = "S3-compatible object storage with console UI.",
                Notes = ["Root password should be at least 8 characters in real deployments."],
                ImageAliases = ["minio/minio"],
                Fields =
                [
                    Text("ROOT_USER", "Root user", "minioadmin", "MINIO_ROOT_USER"),
                    Password("ROOT_PASSWORD", "Root password", "MINIO_ROOT_PASSWORD"),
                    Port("API_PORT", "API host port", "9000", "9000"),
                    Port("CONSOLE_PORT", "Console host port", "9001", "9001")
                ],
                CommandTemplate = "server /data --console-address \":9001\"",
                Volumes = [Volume("minio-data", "/data")]
            },
            new()
            {
                Id = "wordpress",
                DisplayName = "WordPress",
                Category = "CMS",
                Description = "WordPress application container. It normally runs with a MySQL or MariaDB database.",
                Notes =
                [
                    "Use the WordPress stack template when you want the app and database created together.",
                    "Database variables must point to an existing reachable database container."
                ],
                ImageAliases = ["wordpress"],
                Fields =
                [
                    Text("DB_HOST", "Database host", "mysql:3306", "WORDPRESS_DB_HOST"),
                    Text("DB_NAME", "Database name", "wordpress", "WORDPRESS_DB_NAME"),
                    Text("DB_USER", "Database user", "wordpress", "WORDPRESS_DB_USER"),
                    Password("DB_PASSWORD", "Database password", "WORDPRESS_DB_PASSWORD"),
                    Port("HTTP_PORT", "HTTP host port", "8080", "80")
                ],
                Volumes = [Volume("wordpress-data", "/var/www/html")]
            },
            new()
            {
                Id = "nextcloud",
                DisplayName = "Nextcloud",
                Category = "Collaboration",
                Description = "Nextcloud file sharing and collaboration server.",
                Notes =
                [
                    "For a durable setup, run this with a database container and persistent application data.",
                    "The database variables are used when the image performs first-time setup."
                ],
                ImageAliases = ["nextcloud"],
                Fields =
                [
                    Text("MYSQL_HOST", "Database host", "mysql", "MYSQL_HOST"),
                    Text("MYSQL_DATABASE", "Database name", "nextcloud", "MYSQL_DATABASE"),
                    Text("MYSQL_USER", "Database user", "nextcloud", "MYSQL_USER"),
                    Password("MYSQL_PASSWORD", "Database password", "MYSQL_PASSWORD"),
                    Text("ADMIN_USER", "Admin user", "admin", "NEXTCLOUD_ADMIN_USER", required: false),
                    Password("ADMIN_PASSWORD", "Admin password", "NEXTCLOUD_ADMIN_PASSWORD", required: false),
                    Port("HTTP_PORT", "HTTP host port", "8080", "80")
                ],
                Volumes = [Volume("nextcloud-data", "/var/www/html")]
            },
            new()
            {
                Id = "drupal",
                DisplayName = "Drupal",
                Category = "CMS",
                Description = "Drupal CMS application container.",
                Notes =
                [
                    "Drupal database setup is usually completed in the web installer.",
                    "Use persistent volumes for modules, themes, profiles, and sites."
                ],
                ImageAliases = ["drupal"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "8080", "80")
                ],
                Volumes =
                [
                    Volume("modules", "/var/www/html/modules"),
                    Volume("profiles", "/var/www/html/profiles"),
                    Volume("themes", "/var/www/html/themes"),
                    Volume("sites", "/var/www/html/sites")
                ]
            },
            new()
            {
                Id = "gitea",
                DisplayName = "Gitea",
                Category = "Git Service",
                Description = "Self-hosted lightweight Git service.",
                Notes = ["For external SSH clone URLs, keep the SSH host port aligned with your Gitea app settings."],
                ImageAliases = ["gitea/gitea"],
                Fields =
                [
                    Text("USER_UID", "User UID", "1000", "USER_UID"),
                    Text("USER_GID", "User GID", "1000", "USER_GID"),
                    Port("HTTP_PORT", "HTTP host port", "3000", "3000"),
                    Port("SSH_PORT", "SSH host port", "2222", "22")
                ],
                Volumes = [Volume("gitea-data", "/data")]
            },
            new()
            {
                Id = "jenkins",
                DisplayName = "Jenkins",
                Category = "CI/CD",
                Description = "Jenkins automation server with persistent home directory.",
                Notes =
                [
                    "The first admin password is generated inside /var/jenkins_home/secrets/initialAdminPassword.",
                    "Docker-in-Docker or host Docker socket access requires an explicit mount that is not added automatically."
                ],
                ImageAliases = ["jenkins/jenkins"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "8080", "8080"),
                    Port("AGENT_PORT", "Agent host port", "50000", "50000")
                ],
                Volumes = [Volume("jenkins-home", "/var/jenkins_home")]
            },
            new()
            {
                Id = "prometheus",
                DisplayName = "Prometheus",
                Category = "Monitoring",
                Description = "Prometheus metrics database and scraper.",
                Notes =
                [
                    "Real scraping needs a prometheus.yml configuration mounted under /etc/prometheus.",
                    "The default image starts, but useful targets depend on your configuration."
                ],
                ImageAliases = ["prom/prometheus"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "9090", "9090")
                ],
                Volumes =
                [
                    Volume("config", "/etc/prometheus"),
                    Volume("data", "/prometheus")
                ]
            },
            new()
            {
                Id = "node-exporter",
                DisplayName = "Node Exporter",
                Category = "Monitoring",
                Description = "Prometheus exporter for host metrics.",
                Notes =
                [
                    "Host-level metrics usually need explicit host mounts for /proc, /sys, and rootfs.",
                    "Those mounts are not added automatically because they expose host internals."
                ],
                ImageAliases = ["prom/node-exporter"],
                Fields =
                [
                    Port("METRICS_PORT", "Metrics host port", "9100", "9100")
                ]
            },
            new()
            {
                Id = "loki",
                DisplayName = "Loki",
                Category = "Logging",
                Description = "Grafana Loki log aggregation service.",
                Notes =
                [
                    "Production setups normally mount a custom Loki config file.",
                    "Pair this with Promtail or another log shipper to collect logs."
                ],
                ImageAliases = ["grafana/loki"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "3100", "3100")
                ],
                Volumes =
                [
                    Volume("config", "/etc/loki"),
                    Volume("data", "/loki")
                ]
            },
            new()
            {
                Id = "portainer",
                DisplayName = "Portainer CE",
                Category = "Docker Management",
                Description = "Portainer Community Edition management UI.",
                Notes =
                [
                    "Managing the local Docker Engine usually requires mounting /var/run/docker.sock.",
                    "The Docker socket mount is not added automatically because it gives container-level control over the host daemon."
                ],
                ImageAliases = ["portainer/portainer-ce"],
                Fields =
                [
                    Port("HTTPS_PORT", "HTTPS host port", "9443", "9443"),
                    Port("HTTP_PORT", "HTTP host port", "9000", "9000", required: false),
                    Port("EDGE_PORT", "Edge agent host port", "8000", "8000", required: false)
                ],
                Volumes = [Volume("portainer-data", "/data")]
            },
            new()
            {
                Id = "phpmyadmin",
                DisplayName = "phpMyAdmin",
                Category = "Database Tool",
                Description = "Web UI for managing MySQL and MariaDB databases.",
                Notes = ["PMA_HOST must point to an existing MySQL or MariaDB container."],
                ImageAliases = ["phpmyadmin"],
                Fields =
                [
                    Text("PMA_HOST", "Database host", "mysql", "PMA_HOST"),
                    Text("PMA_PORT", "Database port", "3306", "PMA_PORT"),
                    Port("HTTP_PORT", "HTTP host port", "8080", "80")
                ]
            },
            new()
            {
                Id = "adminer",
                DisplayName = "Adminer",
                Category = "Database Tool",
                Description = "Small web UI for managing multiple database engines.",
                Notes = ["Set the default server to the database container name for easier login."],
                ImageAliases = ["adminer"],
                Fields =
                [
                    Text("DEFAULT_SERVER", "Default server", "mysql", "ADMINER_DEFAULT_SERVER", required: false),
                    Port("HTTP_PORT", "HTTP host port", "8081", "8080")
                ]
            },
            new()
            {
                Id = "pgadmin",
                DisplayName = "pgAdmin",
                Category = "Database Tool",
                Description = "PostgreSQL administration web UI.",
                Notes = ["The default login is created only when the pgAdmin data volume is empty."],
                ImageAliases = ["dpage/pgadmin4"],
                Fields =
                [
                    Text("ADMIN_EMAIL", "Admin email", "admin@example.com", "PGADMIN_DEFAULT_EMAIL"),
                    Password("ADMIN_PASSWORD", "Admin password", "PGADMIN_DEFAULT_PASSWORD"),
                    Port("HTTP_PORT", "HTTP host port", "5050", "80")
                ],
                Volumes = [Volume("pgadmin-data", "/var/lib/pgadmin")]
            },
            new()
            {
                Id = "mongo-express",
                DisplayName = "mongo-express",
                Category = "Database Tool",
                Description = "Web UI for browsing and managing MongoDB.",
                Notes = ["MongoDB connection variables must point to an existing reachable MongoDB container."],
                ImageAliases = ["mongo-express"],
                Fields =
                [
                    Text("MONGO_SERVER", "MongoDB host", "mongo", "ME_CONFIG_MONGODB_SERVER"),
                    Text("MONGO_PORT", "MongoDB port", "27017", "ME_CONFIG_MONGODB_PORT"),
                    Text("BASIC_USER", "Basic auth user", "admin", "ME_CONFIG_BASICAUTH_USERNAME", required: false),
                    Password("BASIC_PASSWORD", "Basic auth password", "ME_CONFIG_BASICAUTH_PASSWORD", required: false),
                    Port("HTTP_PORT", "HTTP host port", "8081", "8081")
                ]
            },
            new()
            {
                Id = "kibana",
                DisplayName = "Kibana",
                Category = "Search",
                Description = "Kibana UI for Elasticsearch.",
                Notes = ["ELASTICSEARCH_HOSTS must point to a compatible Elasticsearch node."],
                ImageAliases = ["kibana", "docker.elastic.co/kibana/kibana"],
                Fields =
                [
                    Text("ELASTICSEARCH_HOSTS", "Elasticsearch URL", "http://elasticsearch:9200", "ELASTICSEARCH_HOSTS"),
                    Port("HTTP_PORT", "HTTP host port", "5601", "5601")
                ]
            },
            new()
            {
                Id = "logstash",
                DisplayName = "Logstash",
                Category = "Logging",
                Description = "Log processing pipeline for the Elastic stack.",
                Notes = ["Useful pipelines normally require config files mounted under /usr/share/logstash/pipeline."],
                ImageAliases = ["logstash", "docker.elastic.co/logstash/logstash"],
                Fields =
                [
                    Port("BEATS_PORT", "Beats host port", "5044", "5044", required: false),
                    Port("API_PORT", "API host port", "9600", "9600", required: false)
                ],
                Volumes =
                [
                    Volume("pipeline", "/usr/share/logstash/pipeline"),
                    Volume("config", "/usr/share/logstash/config")
                ]
            },
            new()
            {
                Id = "traefik",
                DisplayName = "Traefik",
                Category = "Reverse Proxy",
                Description = "Modern reverse proxy and edge router.",
                Notes =
                [
                    "Docker provider mode requires mounting the Docker socket explicitly.",
                    "The socket mount is not added automatically because it exposes host Docker control."
                ],
                ImageAliases = ["traefik"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "80", "80"),
                    Port("HTTPS_PORT", "HTTPS host port", "443", "443"),
                    Port("DASHBOARD_PORT", "Dashboard host port", "8080", "8080", required: false)
                ],
                Volumes = [Volume("config", "/etc/traefik")]
            },
            new()
            {
                Id = "haproxy",
                DisplayName = "HAProxy",
                Category = "Reverse Proxy",
                Description = "High-performance TCP and HTTP load balancer.",
                Notes = ["A usable HAProxy container normally needs a mounted haproxy.cfg file."],
                ImageAliases = ["haproxy"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "80", "80"),
                    Port("HTTPS_PORT", "HTTPS host port", "443", "443", required: false)
                ],
                Volumes = [Volume("config", "/usr/local/etc/haproxy")]
            },
            new()
            {
                Id = "mailpit",
                DisplayName = "Mailpit",
                Category = "Developer Tool",
                Description = "Local SMTP testing server with a web inbox.",
                Notes = ["Point application SMTP settings to this container and inspect captured mail in the web UI."],
                ImageAliases = ["axllent/mailpit"],
                Fields =
                [
                    Port("SMTP_PORT", "SMTP host port", "1025", "1025"),
                    Port("WEB_PORT", "Web UI host port", "8025", "8025")
                ]
            },
            new()
            {
                Id = "sonarqube",
                DisplayName = "SonarQube",
                Category = "Code Quality",
                Description = "Code quality and static analysis server.",
                Notes =
                [
                    "For local Docker Desktop use, disabling Elasticsearch bootstrap checks can help first startup.",
                    "Production deployments need an external database and proper host limits."
                ],
                ImageAliases = ["sonarqube"],
                Fields =
                [
                    Boolean("DISABLE_BOOTSTRAP_CHECKS", "Disable bootstrap checks", true, "SONAR_ES_BOOTSTRAP_CHECKS_DISABLE"),
                    Port("HTTP_PORT", "HTTP host port", "9000", "9000")
                ],
                Volumes =
                [
                    Volume("data", "/opt/sonarqube/data"),
                    Volume("extensions", "/opt/sonarqube/extensions"),
                    Volume("logs", "/opt/sonarqube/logs")
                ]
            },
            new()
            {
                Id = "nexus",
                DisplayName = "Nexus Repository",
                Category = "Artifact Repository",
                Description = "Sonatype Nexus Repository Manager.",
                Notes = ["First startup can take a while. The initial admin password is stored inside the data volume."],
                ImageAliases = ["sonatype/nexus3"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "8081", "8081")
                ],
                Volumes = [Volume("nexus-data", "/nexus-data")]
            },
            new()
            {
                Id = "kafka",
                DisplayName = "Apache Kafka",
                Category = "Message Broker",
                Description = "Distributed event streaming broker.",
                Notes =
                [
                    "Kafka networking is sensitive to advertised listeners. Adjust listener values for multi-container or external access.",
                    "This profile targets the Bitnami image because it has practical single-node environment defaults."
                ],
                ImageAliases = ["bitnami/kafka"],
                Fields =
                [
                    Boolean("KRAFT_MODE", "Use KRaft mode", true, "KAFKA_ENABLE_KRAFT", "yes", "no"),
                    Text("NODE_ID", "Node ID", "1", "KAFKA_CFG_NODE_ID"),
                    Text("PROCESS_ROLES", "Process roles", "broker,controller", "KAFKA_CFG_PROCESS_ROLES"),
                    Text("CONTROLLER_QUORUM", "Controller quorum voters", "1@kafka:9093", "KAFKA_CFG_CONTROLLER_QUORUM_VOTERS"),
                    Boolean("ALLOW_PLAINTEXT", "Allow plaintext listener", true, "ALLOW_PLAINTEXT_LISTENER", "yes", "no"),
                    Port("CLIENT_PORT", "Client host port", "9092", "9092")
                ],
                Volumes = [Volume("kafka-data", "/bitnami/kafka")]
            },
            new()
            {
                Id = "zookeeper",
                DisplayName = "ZooKeeper",
                Category = "Coordination",
                Description = "Coordination service used by older Kafka and distributed systems.",
                Notes = ["New Kafka deployments may use KRaft mode instead of ZooKeeper."],
                ImageAliases = ["zookeeper"],
                Fields =
                [
                    Port("CLIENT_PORT", "Client host port", "2181", "2181")
                ],
                Volumes = [Volume("zookeeper-data", "/data"), Volume("zookeeper-log", "/datalog")]
            },
            new()
            {
                Id = "nats",
                DisplayName = "NATS",
                Category = "Message Broker",
                Description = "Lightweight messaging system for pub/sub and request/reply.",
                Notes = ["JetStream persistence needs server options and a persistent store."],
                ImageAliases = ["nats"],
                Fields =
                [
                    Port("CLIENT_PORT", "Client host port", "4222", "4222"),
                    Port("MONITORING_PORT", "Monitoring host port", "8222", "8222", required: false)
                ],
                CommandTemplate = "-js -m 8222",
                Volumes = [Volume("nats-data", "/data")]
            },
            new()
            {
                Id = "vault",
                DisplayName = "HashiCorp Vault",
                Category = "Security",
                Description = "Secrets management server.",
                Notes =
                [
                    "Development mode is convenient but stores data in memory and is not for production.",
                    "For persistent Vault, use a real storage backend and initialization flow."
                ],
                ImageAliases = ["hashicorp/vault", "vault"],
                Fields =
                [
                    Text("DEV_ROOT_TOKEN", "Dev root token", "root", "VAULT_DEV_ROOT_TOKEN_ID"),
                    Port("HTTP_PORT", "HTTP host port", "8200", "8200")
                ],
                CommandTemplate = "server -dev -dev-listen-address=0.0.0.0:8200"
            },
            new()
            {
                Id = "consul",
                DisplayName = "HashiCorp Consul",
                Category = "Service Discovery",
                Description = "Service discovery, health checking, and key-value store.",
                Notes = ["This profile starts a single-node development agent."],
                ImageAliases = ["hashicorp/consul", "consul"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "8500", "8500"),
                    Port("DNS_PORT", "DNS host port", "8600", "8600", required: false)
                ],
                CommandTemplate = "agent -dev -client=0.0.0.0"
            },
            new()
            {
                Id = "influxdb",
                DisplayName = "InfluxDB",
                Category = "Time Series Database",
                Description = "Time-series database for metrics and events.",
                Notes = ["Initial setup variables are used only when the data volume is empty."],
                ImageAliases = ["influxdb"],
                Fields =
                [
                    Boolean("SETUP_MODE", "Run initial setup", true, "DOCKER_INFLUXDB_INIT_MODE", "setup", ""),
                    Text("ADMIN_USER", "Admin user", "admin", "DOCKER_INFLUXDB_INIT_USERNAME"),
                    Password("ADMIN_PASSWORD", "Admin password", "DOCKER_INFLUXDB_INIT_PASSWORD"),
                    Text("ORG", "Organization", "local", "DOCKER_INFLUXDB_INIT_ORG"),
                    Text("BUCKET", "Bucket", "app", "DOCKER_INFLUXDB_INIT_BUCKET"),
                    Port("HTTP_PORT", "HTTP host port", "8086", "8086")
                ],
                Volumes = [Volume("influxdb-data", "/var/lib/influxdb2")]
            },
            new()
            {
                Id = "telegraf",
                DisplayName = "Telegraf",
                Category = "Monitoring",
                Description = "Metrics collection agent for InfluxDB and other outputs.",
                Notes =
                [
                    "Useful Telegraf setups normally mount a telegraf.conf file.",
                    "Host and Docker metrics require explicit host mounts or Docker socket access."
                ],
                ImageAliases = ["telegraf"],
                Fields =
                [
                    Text("INFLUX_URL", "InfluxDB URL", "http://influxdb:8086", "INFLUX_URL", required: false)
                ],
                Volumes = [Volume("config", "/etc/telegraf")]
            },
            new()
            {
                Id = "jaeger",
                DisplayName = "Jaeger",
                Category = "Tracing",
                Description = "All-in-one Jaeger tracing backend and UI.",
                Notes = ["The all-in-one image is best for local development and demos."],
                ImageAliases = ["jaegertracing/all-in-one"],
                Fields =
                [
                    Port("UI_PORT", "UI host port", "16686", "16686"),
                    Port("OTLP_GRPC_PORT", "OTLP gRPC host port", "4317", "4317", required: false),
                    Port("OTLP_HTTP_PORT", "OTLP HTTP host port", "4318", "4318", required: false),
                    Port("COLLECTOR_PORT", "Collector host port", "14268", "14268", required: false)
                ]
            },
            new()
            {
                Id = "opensearch",
                DisplayName = "OpenSearch",
                Category = "Search",
                Description = "OpenSearch search and analytics engine.",
                Notes =
                [
                    "Single-node local use often disables the security plugin for easier startup.",
                    "Linux hosts may need vm.max_map_count tuning for stable operation."
                ],
                ImageAliases = ["opensearchproject/opensearch"],
                Fields =
                [
                    Text("DISCOVERY_TYPE", "Discovery type", "single-node", "discovery.type"),
                    Boolean("DISABLE_SECURITY", "Disable security plugin", true, "DISABLE_SECURITY_PLUGIN"),
                    Text("JAVA_OPTS", "Java options", "-Xms512m -Xmx512m", "OPENSEARCH_JAVA_OPTS"),
                    Port("HTTP_PORT", "HTTP host port", "9200", "9200"),
                    Port("TRANSPORT_PORT", "Transport host port", "9300", "9300")
                ],
                Volumes = [Volume("opensearch-data", "/usr/share/opensearch/data")]
            },
            new()
            {
                Id = "opensearch-dashboards",
                DisplayName = "OpenSearch Dashboards",
                Category = "Search",
                Description = "Dashboard UI for OpenSearch.",
                Notes = ["OPENSEARCH_HOSTS must point to a compatible OpenSearch node."],
                ImageAliases = ["opensearchproject/opensearch-dashboards"],
                Fields =
                [
                    Text("OPENSEARCH_HOSTS", "OpenSearch URLs", "[\"http://opensearch:9200\"]", "OPENSEARCH_HOSTS"),
                    Boolean("DISABLE_SECURITY", "Disable security dashboards plugin", true, "DISABLE_SECURITY_DASHBOARDS_PLUGIN"),
                    Port("HTTP_PORT", "HTTP host port", "5601", "5601")
                ]
            },
            new()
            {
                Id = "memcached",
                DisplayName = "Memcached",
                Category = "Cache",
                Description = "Simple in-memory key-value cache.",
                Notes = ["Memcached has no built-in persistence. Use it for disposable cache data."],
                ImageAliases = ["memcached"],
                Fields =
                [
                    Port("CACHE_PORT", "Cache host port", "11211", "11211")
                ]
            },
            new()
            {
                Id = "cassandra",
                DisplayName = "Apache Cassandra",
                Category = "Database",
                Description = "Distributed wide-column database.",
                Notes =
                [
                    "Single-node Cassandra can take a while to become ready.",
                    "Multi-node clusters need matching seed and network settings."
                ],
                ImageAliases = ["cassandra"],
                Fields =
                [
                    Text("CLUSTER_NAME", "Cluster name", "local-cluster", "CASSANDRA_CLUSTER_NAME"),
                    Text("DATACENTER", "Datacenter", "dc1", "CASSANDRA_DC"),
                    Text("RACK", "Rack", "rack1", "CASSANDRA_RACK"),
                    Port("CQL_PORT", "CQL host port", "9042", "9042")
                ],
                Volumes = [Volume("cassandra-data", "/var/lib/cassandra")]
            },
            new()
            {
                Id = "neo4j",
                DisplayName = "Neo4j",
                Category = "Graph Database",
                Description = "Graph database with browser UI and Bolt protocol.",
                Notes = ["Set NEO4J_AUTH to neo4j/password format, or none for local no-auth testing."],
                ImageAliases = ["neo4j"],
                Fields =
                [
                    Text("AUTH", "Auth value", "neo4j/password", "NEO4J_AUTH"),
                    Port("BROWSER_PORT", "Browser host port", "7474", "7474"),
                    Port("BOLT_PORT", "Bolt host port", "7687", "7687")
                ],
                Volumes =
                [
                    Volume("data", "/data"),
                    Volume("logs", "/logs"),
                    Volume("plugins", "/plugins")
                ]
            },
            new()
            {
                Id = "couchdb",
                DisplayName = "CouchDB",
                Category = "Database",
                Description = "Document database with HTTP API and Fauxton UI.",
                Notes = ["Admin credentials are initialized only when the data directory is empty."],
                ImageAliases = ["couchdb"],
                Fields =
                [
                    Text("ADMIN_USER", "Admin user", "admin", "COUCHDB_USER"),
                    Password("ADMIN_PASSWORD", "Admin password", "COUCHDB_PASSWORD"),
                    Port("HTTP_PORT", "HTTP host port", "5984", "5984")
                ],
                Volumes = [Volume("couchdb-data", "/opt/couchdb/data")]
            },
            new()
            {
                Id = "clickhouse",
                DisplayName = "ClickHouse",
                Category = "Analytics Database",
                Description = "Column-oriented analytics database.",
                Notes = ["Default access management should stay enabled when creating users from environment variables."],
                ImageAliases = ["clickhouse/clickhouse-server"],
                Fields =
                [
                    Text("DB_NAME", "Database name", "app", "CLICKHOUSE_DB"),
                    Text("DB_USER", "Database user", "default", "CLICKHOUSE_USER"),
                    Password("DB_PASSWORD", "Database password", "CLICKHOUSE_PASSWORD", required: false),
                    Boolean("ACCESS_MANAGEMENT", "Enable access management", true, "CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1", "0"),
                    Port("HTTP_PORT", "HTTP host port", "8123", "8123"),
                    Port("NATIVE_PORT", "Native host port", "9000", "9000")
                ],
                Volumes =
                [
                    Volume("data", "/var/lib/clickhouse"),
                    Volume("logs", "/var/log/clickhouse-server")
                ]
            },
            new()
            {
                Id = "meilisearch",
                DisplayName = "Meilisearch",
                Category = "Search",
                Description = "Fast full-text search engine for application search.",
                Notes = ["Use a stable master key for any environment beyond throwaway local testing."],
                ImageAliases = ["getmeili/meilisearch"],
                Fields =
                [
                    Text("ENV", "Environment", "development", "MEILI_ENV"),
                    Password("MASTER_KEY", "Master key", "MEILI_MASTER_KEY", required: false),
                    Port("HTTP_PORT", "HTTP host port", "7700", "7700")
                ],
                Volumes = [Volume("meili-data", "/meili_data")]
            },
            new()
            {
                Id = "typesense",
                DisplayName = "Typesense",
                Category = "Search",
                Description = "Typo-tolerant search engine for application search.",
                Notes = ["Typesense requires an API key for writes and admin operations."],
                ImageAliases = ["typesense/typesense"],
                Fields =
                [
                    Password("API_KEY", "API key", "TYPESENSE_API_KEY"),
                    Text("DATA_DIR", "Data directory", "/data", "TYPESENSE_DATA_DIR"),
                    Boolean("ENABLE_CORS", "Enable CORS", true, "TYPESENSE_ENABLE_CORS"),
                    Port("HTTP_PORT", "HTTP host port", "8108", "8108")
                ],
                Volumes = [Volume("typesense-data", "/data")]
            },
            new()
            {
                Id = "localstack",
                DisplayName = "LocalStack",
                Category = "Cloud Emulator",
                Description = "Local AWS-compatible service emulator.",
                Notes =
                [
                    "Set SERVICES to the AWS services you need, such as s3,sqs,dynamodb.",
                    "Some workflows may need additional host mappings or provider-specific options."
                ],
                ImageAliases = ["localstack/localstack"],
                Fields =
                [
                    Text("SERVICES", "Services", "s3,sqs,dynamodb", "SERVICES"),
                    Boolean("DEBUG", "Enable debug logs", false, "DEBUG", "1", "0"),
                    Port("EDGE_PORT", "Edge host port", "4566", "4566")
                ],
                Volumes = [Volume("localstack-data", "/var/lib/localstack")]
            },
            new()
            {
                Id = "otel-collector",
                DisplayName = "OpenTelemetry Collector",
                Category = "Observability",
                Description = "OpenTelemetry collector for traces, metrics, and logs.",
                Notes = ["Useful collector pipelines require a mounted config file at /etc/otelcol-contrib/config.yaml."],
                ImageAliases = ["otel/opentelemetry-collector-contrib"],
                Fields =
                [
                    Port("OTLP_GRPC_PORT", "OTLP gRPC host port", "4317", "4317"),
                    Port("OTLP_HTTP_PORT", "OTLP HTTP host port", "4318", "4318"),
                    Port("METRICS_PORT", "Internal metrics host port", "8888", "8888", required: false)
                ],
                CommandTemplate = "--config=/etc/otelcol-contrib/config.yaml",
                Volumes = [Volume("config", "/etc/otelcol-contrib")]
            },
            new()
            {
                Id = "promtail",
                DisplayName = "Promtail",
                Category = "Logging",
                Description = "Log shipper for sending logs to Loki.",
                Notes =
                [
                    "Promtail needs a config file and log file mounts to collect real host or container logs.",
                    "Host log mounts are not added automatically."
                ],
                ImageAliases = ["grafana/promtail"],
                CommandTemplate = "-config.file=/etc/promtail/config.yml",
                Volumes = [Volume("config", "/etc/promtail")]
            },
            new()
            {
                Id = "fluent-bit",
                DisplayName = "Fluent Bit",
                Category = "Logging",
                Description = "Lightweight log processor and forwarder.",
                Notes =
                [
                    "Real log collection usually needs mounted input files or host paths.",
                    "Mount custom config under /fluent-bit/etc when the default pipeline is not enough."
                ],
                ImageAliases = ["fluent/fluent-bit"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP server host port", "2020", "2020", required: false)
                ],
                Volumes = [Volume("config", "/fluent-bit/etc")]
            },
            new()
            {
                Id = "registry",
                DisplayName = "Docker Registry",
                Category = "Registry",
                Description = "Private Docker image registry.",
                Notes =
                [
                    "This is the basic registry service only. Authentication and TLS require additional config.",
                    "Use a persistent volume so pushed images survive container recreation."
                ],
                ImageAliases = ["registry"],
                Fields =
                [
                    Port("REGISTRY_PORT", "Registry host port", "5000", "5000")
                ],
                Volumes = [Volume("registry-data", "/var/lib/registry")]
            },
            new()
            {
                Id = "nginx-proxy-manager",
                DisplayName = "Nginx Proxy Manager",
                Category = "Reverse Proxy",
                Description = "Web UI for managing Nginx reverse proxies and certificates.",
                Notes =
                [
                    "Initial login and certificate setup are completed in the web UI.",
                    "Public certificates need DNS and reachable HTTP/HTTPS ports."
                ],
                ImageAliases = ["jc21/nginx-proxy-manager"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "80", "80"),
                    Port("ADMIN_PORT", "Admin UI host port", "81", "81"),
                    Port("HTTPS_PORT", "HTTPS host port", "443", "443")
                ],
                Volumes =
                [
                    Volume("data", "/data"),
                    Volume("letsencrypt", "/etc/letsencrypt")
                ]
            },
            new()
            {
                Id = "cloudbeaver",
                DisplayName = "CloudBeaver",
                Category = "Database Tool",
                Description = "Browser-based database management tool.",
                Notes = ["Database connections are configured in the CloudBeaver web UI."],
                ImageAliases = ["dbeaver/cloudbeaver"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "8978", "8978")
                ],
                Volumes = [Volume("workspace", "/opt/cloudbeaver/workspace")]
            },
            new()
            {
                Id = "metabase",
                DisplayName = "Metabase",
                Category = "Business Intelligence",
                Description = "Business intelligence dashboard and query tool.",
                Notes =
                [
                    "The embedded database is fine for local testing.",
                    "Use an external application database for real shared deployments."
                ],
                ImageAliases = ["metabase/metabase"],
                Fields =
                [
                    Text("DB_FILE", "Embedded DB file", "/metabase-data/metabase.db", "MB_DB_FILE"),
                    Port("HTTP_PORT", "HTTP host port", "3000", "3000")
                ],
                Volumes = [Volume("metabase-data", "/metabase-data")]
            },
            new()
            {
                Id = "n8n",
                DisplayName = "n8n",
                Category = "Automation",
                Description = "Workflow automation tool.",
                Notes =
                [
                    "Set a stable encryption key before storing credentials.",
                    "Production deployments usually use an external database and queue mode."
                ],
                ImageAliases = ["n8nio/n8n"],
                Fields =
                [
                    Text("TIMEZONE", "Timezone", "Asia/Seoul", "GENERIC_TIMEZONE"),
                    Password("ENCRYPTION_KEY", "Encryption key", "N8N_ENCRYPTION_KEY", required: false),
                    Port("HTTP_PORT", "HTTP host port", "5678", "5678")
                ],
                Volumes = [Volume("n8n-data", "/home/node/.n8n")]
            },
            new()
            {
                Id = "hasura",
                DisplayName = "Hasura GraphQL Engine",
                Category = "API Gateway",
                Description = "Instant GraphQL API layer for PostgreSQL.",
                Notes = ["DATABASE_URL must point to an existing PostgreSQL database."],
                ImageAliases = ["hasura/graphql-engine"],
                Fields =
                [
                    Text("DATABASE_URL", "PostgreSQL URL", "postgres://postgres:password@postgres:5432/postgres", "HASURA_GRAPHQL_DATABASE_URL"),
                    Boolean("ENABLE_CONSOLE", "Enable console", true, "HASURA_GRAPHQL_ENABLE_CONSOLE"),
                    Boolean("DEV_MODE", "Development mode", true, "HASURA_GRAPHQL_DEV_MODE"),
                    Password("ADMIN_SECRET", "Admin secret", "HASURA_GRAPHQL_ADMIN_SECRET", required: false),
                    Port("HTTP_PORT", "HTTP host port", "8080", "8080")
                ]
            },
            new()
            {
                Id = "directus",
                DisplayName = "Directus",
                Category = "Headless CMS",
                Description = "Headless CMS and data API platform.",
                Notes =
                [
                    "This profile uses SQLite for a simple local start.",
                    "Use stable KEY and SECRET values before creating real projects."
                ],
                ImageAliases = ["directus/directus"],
                Fields =
                [
                    Password("KEY", "App key", "KEY"),
                    Password("SECRET", "App secret", "SECRET"),
                    Text("ADMIN_EMAIL", "Admin email", "admin@example.com", "ADMIN_EMAIL"),
                    Password("ADMIN_PASSWORD", "Admin password", "ADMIN_PASSWORD"),
                    Text("DB_CLIENT", "Database client", "sqlite3", "DB_CLIENT"),
                    Text("DB_FILENAME", "SQLite file", "/directus/database/data.db", "DB_FILENAME"),
                    Port("HTTP_PORT", "HTTP host port", "8055", "8055")
                ],
                Volumes =
                [
                    Volume("database", "/directus/database"),
                    Volume("uploads", "/directus/uploads"),
                    Volume("extensions", "/directus/extensions")
                ]
            },
            new()
            {
                Id = "wiremock",
                DisplayName = "WireMock",
                Category = "Testing",
                Description = "HTTP API mocking server.",
                Notes = ["Mount mappings and __files to keep mock definitions under version control."],
                ImageAliases = ["wiremock/wiremock"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "8080", "8080")
                ],
                CommandTemplate = "--global-response-templating",
                Volumes =
                [
                    Volume("mappings", "/home/wiremock/mappings"),
                    Volume("files", "/home/wiremock/__files")
                ]
            },
            new()
            {
                Id = "mockserver",
                DisplayName = "MockServer",
                Category = "Testing",
                Description = "HTTP and HTTPS mock server for integration tests.",
                Notes = ["Expectations can be configured through the MockServer API or mounted initializer files."],
                ImageAliases = ["mockserver/mockserver"],
                Fields =
                [
                    Text("LOG_LEVEL", "Log level", "INFO", "MOCKSERVER_LOG_LEVEL"),
                    Port("HTTP_PORT", "HTTP host port", "1080", "1080")
                ]
            },
            new()
            {
                Id = "selenium-chrome",
                DisplayName = "Selenium Chrome",
                Category = "Testing",
                Description = "Standalone Chrome browser node for Selenium tests.",
                Notes =
                [
                    "Browser automation may need more shared memory than the default Docker setting.",
                    "No host browser window is opened; use WebDriver or the noVNC port."
                ],
                ImageAliases = ["selenium/standalone-chrome"],
                Fields =
                [
                    Boolean("NO_VNC_PASSWORD", "Disable noVNC password", true, "SE_VNC_NO_PASSWORD"),
                    Port("WEBDRIVER_PORT", "WebDriver host port", "4444", "4444"),
                    Port("NOVNC_PORT", "noVNC host port", "7900", "7900", required: false)
                ]
            },
            new()
            {
                Id = "qdrant",
                DisplayName = "Qdrant",
                Category = "Vector Database",
                Description = "Vector database for semantic search and AI applications.",
                Notes = ["Persist /qdrant/storage to keep collections across container recreation."],
                ImageAliases = ["qdrant/qdrant"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "6333", "6333"),
                    Port("GRPC_PORT", "gRPC host port", "6334", "6334", required: false)
                ],
                Volumes = [Volume("qdrant-storage", "/qdrant/storage")]
            },
            new()
            {
                Id = "ollama",
                DisplayName = "Ollama",
                Category = "AI Runtime",
                Description = "Local LLM runtime and model server.",
                Notes =
                [
                    "Models are downloaded after the container starts, for example with ollama pull.",
                    "GPU acceleration requires extra Docker runtime options that are not added automatically."
                ],
                ImageAliases = ["ollama/ollama"],
                Fields =
                [
                    Port("HTTP_PORT", "HTTP host port", "11434", "11434")
                ],
                Volumes = [Volume("ollama-data", "/root/.ollama")]
            },
            new()
            {
                Id = "grafana",
                DisplayName = "Grafana",
                Category = "Monitoring",
                Description = "Grafana dashboard server with initial admin credentials.",
                Notes = ["Datasource provisioning is separate. Pair with Prometheus, Loki, or the monitoring stack template."],
                ImageAliases = ["grafana/grafana", "grafana/grafana-oss"],
                Fields =
                [
                    Text("ADMIN_USER", "Admin user", "admin", "GF_SECURITY_ADMIN_USER"),
                    Password("ADMIN_PASSWORD", "Admin password", "GF_SECURITY_ADMIN_PASSWORD"),
                    Port("HOST_PORT", "Host port", "3000", "3000")
                ],
                Volumes = [Volume("grafana-data", "/var/lib/grafana")]
            },
            new()
            {
                Id = "keycloak",
                DisplayName = "Keycloak",
                Category = "Identity",
                Description = "Keycloak identity server in development mode.",
                Notes = ["start-dev is for local development, not production."],
                ImageAliases = ["keycloak/keycloak"],
                Fields =
                [
                    Text("ADMIN_USER", "Admin user", "admin", "KC_BOOTSTRAP_ADMIN_USERNAME"),
                    Password("ADMIN_PASSWORD", "Admin password", "KC_BOOTSTRAP_ADMIN_PASSWORD"),
                    Port("HOST_PORT", "Host port", "8080", "8080")
                ],
                CommandTemplate = "start-dev"
            },
            new()
            {
                Id = "sqlserver",
                DisplayName = "Microsoft SQL Server",
                Category = "Database",
                Description = "SQL Server with EULA acceptance and sa password.",
                Notes = ["SA password must satisfy SQL Server complexity rules."],
                ImageAliases = ["mssql/server"],
                Fields =
                [
                    Boolean("ACCEPT_EULA", "Accept Microsoft EULA", true, "ACCEPT_EULA", "Y", "N"),
                    Password("SA_PASSWORD", "sa password", "MSSQL_SA_PASSWORD"),
                    Text("EDITION", "Edition / PID", "Developer", "MSSQL_PID"),
                    Port("HOST_PORT", "Host port", "1433", "1433")
                ],
                Volumes = [Volume("mssql-data", "/var/opt/mssql")]
            },
            new()
            {
                Id = "elasticsearch",
                DisplayName = "Elasticsearch",
                Category = "Search",
                Description = "Single-node Elasticsearch with JVM memory options.",
                Notes = ["Elasticsearch usually needs enough memory and vm.max_map_count on Linux hosts."],
                ImageAliases = ["elasticsearch/elasticsearch"],
                Fields =
                [
                    Text("NODE_MODE", "Discovery type", "single-node", "discovery.type"),
                    Boolean("SECURITY_ENABLED", "Enable security", true, "xpack.security.enabled"),
                    Password("ELASTIC_PASSWORD", "elastic password", "ELASTIC_PASSWORD"),
                    Text("JAVA_OPTS", "JVM options", "-Xms512m -Xmx512m", "ES_JAVA_OPTS"),
                    Port("HTTP_PORT", "HTTP host port", "9200", "9200"),
                    Port("TRANSPORT_PORT", "Transport host port", "9300", "9300")
                ],
                Volumes = [Volume("elasticsearch-data", "/usr/share/elasticsearch/data")]
            }
        ];

        public static IReadOnlyList<ContainerImageProfile> GetAll() => Profiles;

        public static ContainerImageProfile? Find(string imageReference)
        {
            string repository = NormalizeRepository(imageReference);
            if (string.IsNullOrEmpty(repository))
                return null;

            foreach (var profile in Profiles)
            {
                foreach (string alias in profile.ImageAliases)
                {
                    if (MatchesAlias(repository, alias))
                        return profile;
                }
            }

            return null;
        }

        private static bool MatchesAlias(string repository, string alias)
        {
            if (repository.Equals(alias, StringComparison.OrdinalIgnoreCase))
                return true;

            if (alias.Contains('/'))
                return repository.EndsWith("/" + alias, StringComparison.OrdinalIgnoreCase);

            if (repository.Equals("library/" + alias, StringComparison.OrdinalIgnoreCase) ||
                repository.EndsWith("/library/" + alias, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            int firstSlash = repository.IndexOf('/');
            if (firstSlash <= 0 || repository.IndexOf('/', firstSlash + 1) >= 0)
                return false;

            string registry = repository[..firstSlash];
            string image = repository[(firstSlash + 1)..];
            bool looksLikeRegistry =
                registry.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                registry.Contains('.') ||
                registry.Contains(':');

            return looksLikeRegistry &&
                   image.Equals(alias, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRepository(string imageReference)
        {
            string value = imageReference.Trim().Replace('\\', '/');
            int digestIndex = value.IndexOf('@');
            if (digestIndex >= 0)
                value = value[..digestIndex];

            int lastSlash = value.LastIndexOf('/');
            int lastColon = value.LastIndexOf(':');
            if (lastColon > lastSlash)
                value = value[..lastColon];

            return value.ToLowerInvariant();
        }

        private static ContainerImageProfileField Text(
            string key,
            string label,
            string defaultValue,
            string environmentVariable,
            string helpText = "",
            bool required = true) =>
            new()
            {
                Key = key,
                Label = label,
                DefaultValue = defaultValue,
                EnvironmentVariable = environmentVariable,
                HelpText = helpText,
                Required = required
            };

        private static ContainerImageProfileField Password(
            string key,
            string label,
            string environmentVariable = "",
            string helpText = "",
            bool required = true) =>
            new()
            {
                Key = key,
                Label = label,
                Type = "password",
                EnvironmentVariable = environmentVariable,
                HelpText = helpText,
                Required = required
            };

        private static ContainerImageProfileField Port(
            string key,
            string label,
            string defaultValue,
            string containerPort,
            bool required = true) =>
            new()
            {
                Key = key,
                Label = label,
                Type = "port",
                DefaultValue = defaultValue,
                ContainerPort = containerPort,
                Required = required
            };

        private static ContainerImageProfileField Boolean(
            string key,
            string label,
            bool defaultValue,
            string environmentVariable,
            string trueValue = "true",
            string falseValue = "false") =>
            new()
            {
                Key = key,
                Label = label,
                Type = "bool",
                DefaultValue = defaultValue ? "true" : "false",
                EnvironmentVariable = environmentVariable,
                TrueValue = trueValue,
                FalseValue = falseValue
            };

        private static ContainerImageProfileVolume Volume(string suffix, string target) =>
            new()
            {
                NameSuffix = suffix,
                ContainerPath = target
            };
    }
}
