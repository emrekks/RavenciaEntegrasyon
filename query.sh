sudo docker exec marketplacehub-postgres-1 psql -U marketplacehub -d marketplacehub -c "SELECT \"OrderNumber\", \"DerivedStatus\" FROM sales.orders WHERE \"OrderNumber\" = '1927642568';"
