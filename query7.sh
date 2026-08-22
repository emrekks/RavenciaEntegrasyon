sudo docker exec marketplacehub-postgres-1 psql -U marketplacehub -d marketplacehub -x -c "SELECT \"CustomerSnapshotJson\" FROM sales.orders WHERE \"OrderNumber\" = '1927642568';"
